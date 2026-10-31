using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using SimModel = Allegiance.Core.Sim.Model;

namespace Allegiance.Core.Tests;

public sealed class P906TickOrderTests
{
    private sealed class Site : IIgcSite
    {
        public readonly List<(byte Type, double Time, Vector3 Position)> Damage = [];
        public readonly List<double> Kills = [];
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2) => Kills.Add(now);
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher, byte type,
            float amount, float leakage, Vector3 p1, Vector3 p2) => Damage.Add((type, now, p1));
    }
    private sealed class Observer(short id, Action action) : SimModel(id, Vector3.Zero)
    {
        protected override void OnUpdate(double now, double dt) => action();
    }
    private static DataHullTypeIGC Hull(float hp = 1000, float thrust = 0) => DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
    {
        HitPoints = hp, Length = 10, DefenseType = 0, Mass = 1, Speed = 100, Thrust = thrust,
        SideMultiplier = 1, BackMultiplier = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1]
    };
    private static (Mission, Site) Domain(bool phases = false, bool queue = false, bool warp = false)
    {
        var body = new byte[DataConstantsIGC.Size];
        for (int i = 0; i < 400; i++) BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(DataConstantsIGC.DamageOffset + 4 * i), 1);
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Body = body, BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, body.Length, 0)] });
        var site = new Site();
        var mission = Mission.Assemble(core, MissionMap.FromLists([
            DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0, Name = "P906" }], warp ? [DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
                { WarpId = 10, DestinationId = 11, ClusterId = 0, Radius = 5, Position = [0, 0, 0], Forward = [0, 0, -1] }] : [], [], []), [], site);
        mission.Stage = MissionStage.Started;
        mission.CrowdingPenaltyEnabled = false;
        mission.NativeShipPhasesEnabled = phases;
        mission.NativeCollisionQueueEnabled = queue;
        return (mission, site);
    }
    private static Ship Place(Mission m, short id, float x, float vx, float hp = 1000, float thrust = 0)
    {
        var ship = new Ship(id, Hull(hp, thrust), new(x, 0, 0), new(vx, 0, 0), Orientation.Identity)
            { Controls = new(0, 0, 0, -1), StateM = ShipControlState.CoastButton };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }
    private static Projectile Round(Mission m, short id, float x, float vx, double born = 0)
    {
        var round = new Projectile(id, m.Sectors[0], DataProjectileTypeIGC.Decode(new byte[DataProjectileTypeIGC.Size]) with
            { Radius = 1, Power = 100, DamageType = 3, Lifespan = 10 }, new(x, 0, 0), new(vx, 0, 0), new(1, 0, 0), born);
        m.Sectors[0].Add(round);
        return round;
    }
    private static void Rock(Mission m, float x)
    {
        m.PlaceAsteroid(new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
            { AsteroidId = 50, ClusterId = 0, Position = [x, 0, 0], Radius = 5, HitPoints = 10000 }), m.Sectors[0]);
    }

    [Fact]
    public void All_three_server_flags_and_new_mission_flags_default_off()
    {
        var sim = new SimConfig();
        Assert.False(sim.NativeShipPhases); Assert.False(sim.NativeCollisionQueue); Assert.False(sim.NativeBroadcastOrder);
        var (m, _) = Domain();
        Assert.False(m.NativeShipPhasesEnabled); Assert.False(m.NativeCollisionQueueEnabled);
    }

    // clusterIGC.cpp:170-231,241-253: the ordinary model sees EVERY ship's
    // executed velocity even if that model precedes both ships in the model list.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All_ship_execute_precedes_ordinary_models_only_when_enabled(bool enabled)
    {
        var (m, _) = Domain(phases: enabled);
        Ship? a = null, b = null;
        Vector3 seenA = default, seenB = default;
        m.Sectors[0].Add(new Observer(90, () => { seenA = a!.Velocity; seenB = b!.Velocity; }));
        a = Place(m, 1, -1000, 10, thrust: 10); b = Place(m, 2, 1000, -10, thrust: 10);
        a.StateM = b.StateM = 0;
        m.Sectors[0].Update(.1);
        Assert.NotEqual(new Vector3(10, 0, 0), a.Velocity);
        Assert.Equal(enabled ? a.Velocity : new Vector3(10, 0, 0), seenA);
        Assert.Equal(enabled ? b.Velocity : new Vector3(-10, 0, 0), seenB);
        Assert.Equal(a.Velocity * .1f + new Vector3(-1000, 0, 0), a.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Burnout_blast_before_ship_update_observes_executed_velocity_only_with_global_phases(bool enabled)
    {
        var (m, site) = Domain(phases: enabled);
        var sector = m.Sectors[0];
        var round = new Projectile(2, sector, DataProjectileTypeIGC.Decode(new byte[DataProjectileTypeIGC.Size]) with
            { Radius = 1, BlastPower = 10000, BlastRadius = 1000, Lifespan = .05f },
            Vector3.Zero, Vector3.Zero, new(1, 0, 0), 0);
        sector.Add(round); // ordinary model precedes the ship
        var ship = Place(m, 1, 0, 10, hp: 100, thrust: 10);
        ship.StateM = 0;
        sector.Update(.1);
        Assert.True(ship.Dead);
        Assert.Single(site.Kills);
        Assert.Equal(!enabled, ship.Velocity == new Vector3(10, 0, 0));
        Assert.DoesNotContain(ship, sector.Models); // later model must not update after death
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Earlier_rock_contact_preempts_later_lethal_round_only_in_time_queue(bool enabled)
    {
        var (m, site) = Domain(queue: enabled);
        var target = Place(m, 1, 0, 10, hp: 1);
        Rock(m, 15);
        var round = Round(m, 2, -15, 20);
        m.Sectors[0].Update(1);
        Assert.True(target.Dead);
        Assert.Empty(site.Damage);
        Assert.Equal(enabled ? .5 : 1, Assert.Single(site.Kills), 5);
        Assert.Equal(enabled, m.Sectors[0].Models.Contains(round));
    }

    [Fact]
    public void Bounce_recalculates_tail_and_discovers_previously_absent_projectile_contact()
    {
        var (m, site) = Domain(queue: true);
        var target = Place(m, 1, 0, 10);
        Rock(m, 15);
        var round = Round(m, 2, -30, 30);
        m.Sectors[0].Update(1);
        Assert.True(target.Velocity.X < 0);
        Assert.DoesNotContain(round, m.Sectors[0].Models);
        Assert.Equal(new byte[] { 0, 3 }, site.Damage.Select(d => d.Type));
        Assert.Equal(.5, site.Damage[0].Time, 5);
        Assert.InRange(site.Damage[1].Time, .8, 1);
        Assert.InRange(target.Position.X, .7f, 1.3f);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Inactive_or_not_started_queue_neither_collides_nor_moves(bool active, bool started)
    {
        var (m, site) = Domain(queue: true);
        var target = Place(m, 1, 0, 10);
        Round(m, 2, -15, 20);
        m.Sectors[0].Active = active;
        if (!started) m.Stage = MissionStage.Starting;
        m.Sectors[0].Update(1);
        Assert.Equal(Vector3.Zero, target.Position);
        Assert.Empty(site.Damage);
    }

    [Fact]
    public void Newborn_round_is_not_swept_back_before_its_creation_and_moves_only_its_age()
    {
        var (m, site) = Domain(queue: true);
        var target = Place(m, 1, 0, 0);
        Projectile? born = null;
        m.Sectors[0].Add(new Observer(90, () => born = Round(m, 2, 10, 40, .75)));
        m.Sectors[0].Update(1);
        Assert.Empty(site.Damage);
        Assert.Equal(new Vector3(20, 0, 0), born!.Position);
        Assert.False(target.Dead);
    }

    [Fact]
    public void Round_born_inside_target_hits_at_creation_instead_of_before_birth_or_not_at_all()
    {
        var (m, site) = Domain(queue: true);
        Place(m, 1, 0, 0);
        m.Sectors[0].Add(new Observer(90, () => Round(m, 2, 0, 40, .75)));
        m.Sectors[0].Update(1);
        Assert.Equal(.75, Assert.Single(site.Damage).Time, 5);
    }

    [Fact]
    public void Starting_stage_does_not_execute_ship_phase_passes()
    {
        var (m, _) = Domain(phases: true);
        var ship = Place(m, 1, 0, 10, thrust: 10);
        ship.StateM = 0;
        m.Stage = MissionStage.Starting;
        m.Sectors[0].Update(.1);
        Assert.Equal(new Vector3(10, 0, 0), ship.Velocity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Earlier_aleph_contact_consumes_missile_before_later_ship_contact(bool enabled)
    {
        var (m, _) = Domain(queue: enabled, warp: true);
        var launcher = Place(m, 1, 1000, 0); launcher.SideId = 0;
        var target = Place(m, 2, 0, 0, hp: 1); target.Position = new(0, 0, -15); target.SideId = 1;
        var missile = new Missile(3, m.Sectors[0], DataMissileTypeIGC.Decode(new byte[DataMissileTypeIGC.Size]) with
            { Radius = 1, Power = 100, HitPoints = 100, Lifespan = 100, ReadyTime = 0 },
            new(0, 0, 20), new(0, 0, -30), new(0, 0, -1), 0, launcher);
        m.Sectors[0].Add(missile);
        m.Sectors[0].Update(1);
        Assert.Equal(!enabled, target.Dead);
        Assert.DoesNotContain(missile, m.Sectors[0].Models);
    }

    [Fact]
    public void Swept_mine_contact_is_applied_before_final_movement()
    {
        var (m, site) = Domain(queue: true);
        var sector = m.Sectors[0]; sector.Update(2);
        var ship = Place(m, 1, -30, 60, hp: 1);
        var mine = new Mine(2, sector, DataMineTypeIGC.Decode(new byte[DataMineTypeIGC.Size]) with
            { MineRadius = 5, Power = 1000, Endurance = 10000, Lifespan = 100 }, Vector3.Zero, 0);
        sector.Add(mine);
        sector.Update(3);
        Assert.True(ship.Dead);
        Assert.Equal(2 + 1d / 3, Assert.Single(site.Kills), 5);
        Assert.Equal(new Vector3(-30, 0, 0), ship.Position);
    }

    [Fact]
    public void Simultaneous_treasure_contacts_keep_insertion_order_and_pick_only_once()
    {
        var (m, _) = Domain(queue: true);
        var hull = Hull() with { HabmCapabilities = DataHullTypeIGC.HabmRescue };
        var first = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var second = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        m.PlaceShip(first, m.Sectors[0]); m.PlaceShip(second, m.Sectors[0]);
        var treasure = new Treasure(3, m.Sectors[0], new([0, 0, 0], [0, 0, 0], 100, 0, 3, 0, 0, 10, TreasureCode.Cash, true), 0);
        m.Sectors[0].Add(treasure);
        m.Sectors[0].Update(.1);
        Assert.Same(first, Assert.Single(m.Sectors[0].DrainTreasurePickups()).Ship);
    }

    [Fact]
    public void Pending_probe_rescue_excludes_pod_from_later_contacts_and_final_movement()
    {
        var (m, _) = Domain(queue: true);
        var pod = new Ship(1, Hull() with { HabmCapabilities = DataHullTypeIGC.HabmLifepod },
            new(-15, 0, 0), new(20, 0, 0), Orientation.Identity)
            { SideId = 0, StateM = ShipControlState.CoastButton };
        m.PlaceShip(pod, m.Sectors[0]);
        var type = DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]) with
            { Radius = 5, HitPoints = 100, Lifespan = 100, ProjectileTypeId = -1, EabmCapabilities = DataProbeTypeIGC.EabmRescueAny };
        m.Sectors[0].Add(new Probe(2, m.Sectors[0], type, Vector3.Zero, 0, 1));
        m.Sectors[0].Add(new Probe(3, m.Sectors[0], type, Vector3.Zero, 0, 1));
        m.Sectors[0].Update(1);
        Assert.Same(pod, Assert.Single(m.Sectors[0].DrainStationRescues()).Rescuee);
        Assert.Equal(new Vector3(-15, 0, 0), pod.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Boarded_ship_does_not_execute_an_independent_move_in_global_phases(bool enabled)
    {
        var (m, _) = Domain(phases: enabled);
        var parent = Place(m, 1, 1000, 10);
        var child = Place(m, 2, 0, 5);
        child.ParentShip = parent;
        m.Sectors[0].Update(1);
        Assert.Equal(new Vector3(enabled ? 0 : 5, 0, 0), child.Position);
    }

    [Fact]
    public void Tick_end_newborn_contact_uses_native_boundary_window_and_legacy_api_still_rejects_zero()
    {
        var (m, site) = Domain(queue: true);
        Place(m, 1, 0, 0);
        m.Sectors[0].Add(new Observer(90, () => Round(m, 2, 0, 40, 1)));
        m.Sectors[0].Update(1);
        Assert.Equal(1, Assert.Single(site.Damage).Time);
        var a = new Collider(null, Vector3.Zero, Vector3.Zero, Orientation.Identity, null, 1, false);
        var b = new Collider(null, Vector3.Zero, Vector3.Zero, Orientation.Identity, null, 1, false);
        Assert.Throws<ArgumentOutOfRangeException>(() => NarrowPhase.TestPair(a, b, 0));
    }

    [Fact]
    public void Recalculation_at_tick_end_keeps_other_simultaneous_contacts()
    {
        var (m, site) = Domain(queue: true);
        var first = Place(m, 1, 0, 10);
        var second = Place(m, 2, -100, -10);
        first.Velocity = new(6, 8, 0); second.Velocity = new(-6, -8, 0);
        // Diagonal sphere contacts have strictly overlapping AABBs at t=1;
        // an axis-aligned exact touch would fail native's strict broadphase.
        foreach (var (id, x, y) in new[] { (50, 12, 16), (51, -112, -16) })
            m.PlaceAsteroid(new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
                { AsteroidId = (short)id, ClusterId = 0, Position = [x, y, 0], Radius = 5, HitPoints = 10000 }), m.Sectors[0]);
        m.Sectors[0].Update(1);
        Assert.True(first.Velocity.X < 0);
        Assert.True(second.Velocity.X > 0);
        Assert.Equal(2, site.Damage.Count);
        Assert.All(site.Damage, d => Assert.Equal(1, d.Time, 5));
    }

    [Fact]
    public void Stage_change_in_model_callback_does_not_cancel_a_tick_that_started_running()
    {
        var (m, site) = Domain(queue: true);
        m.Sectors[0].Add(new Observer(90, () => m.Stage = MissionStage.Over));
        Place(m, 1, 0, 0);
        var round = Round(m, 2, -10, 10);
        m.Sectors[0].Update(1);
        Assert.Equal(.4, Assert.Single(site.Damage).Time, 5);
        Assert.DoesNotContain(round, m.Sectors[0].Models);
    }

    [Fact]
    public void Duplicate_and_reverse_time_do_not_repeat_phases_or_movement()
    {
        var (m, _) = Domain(phases: true, queue: true);
        var ship = Place(m, 1, 0, 10);
        m.Sectors[0].Update(1);
        var position = ship.Position;
        m.Sectors[0].Update(1); m.Sectors[0].Update(.5);
        Assert.Equal(position, ship.Position);
        Assert.Equal(1, m.Sectors[0].UpdateCount);
    }
}
