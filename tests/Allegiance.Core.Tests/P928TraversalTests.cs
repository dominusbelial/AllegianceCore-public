using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P928TraversalTests
{
    private static Mission World(IIgcSite? site = null)
    {
        var body = new byte[DataConstantsIGC.Size];
        var core = StaticCore.Load(new IgcFile { Wrapper = IgcWrapper.VersionAndDataSize, Version = 1,
            Body = body, BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, body.Length, 0)] });
        var mission = Mission.Assemble(core, MissionMap.FromLists([
            DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0, Name = "P928" }], [], [], []), [], site ?? new NullIgcSite());
        mission.Stage = MissionStage.Started;
        mission.CrowdingPenaltyEnabled = false;
        return mission;
    }
    private sealed class CallbackModel(short id, Action action) : Allegiance.Core.Sim.Model(id, Vector3.Zero)
    { protected override void OnUpdate(double now, double dt) => action(); }

    [Fact]
    public void Snapshot_skips_removed_models_defers_additions_and_survives_nested_resolver()
    {
        using var mission = World();
        var sector = mission.Sectors[0];
        var calls = new List<int>();
        var b = new CallbackModel(2, () => calls.Add(2));
        var c = new CallbackModel(3, () => calls.Add(3));
        bool first = true;
        sector.Add(new CallbackModel(1, () =>
        {
            calls.Add(1);
            if (first) { first = false; sector.Remove(b); sector.Add(c); }
            sector.ResolveRipcordCountdowns(.053f); // nested snapshot must not overwrite the outer one
        }));
        sector.Add(b);
        sector.Update(.053);
        Assert.Equal(new[] { 1 }, calls);
        calls.Clear();
        sector.Update(.106);
        Assert.Equal(new[] { 1, 3 }, calls);
    }

    [Fact]
    public void Snapshot_is_released_when_a_model_callback_throws()
    {
        using var mission = World();
        var sector = mission.Sectors[0];
        bool throws = true;
        int later = 0;
        sector.Add(new CallbackModel(1, () => { if (throws) { throws = false; throw new InvalidOperationException("test callback"); } }));
        sector.Add(new CallbackModel(2, () => later++));
        Assert.Throws<InvalidOperationException>(() => sector.Update(.053));
        sector.Update(.106);
        sector.Update(.159);
        Assert.Equal(2, later);
    }

    private sealed class FireSite : IIgcSite
    {
        public readonly List<short> Order = [];
        public Action<short>? OnShot;
        public void ProjectileCreatedEvent(Projectile projectile)
        { Order.Add(projectile.ProjectileType.ProjectileTypeId); OnShot?.Invoke(projectile.ProjectileType.ProjectileTypeId); }
    }

    [Fact]
    public void Weapon_updates_keep_first_fixed_match_stable_turret_ties_and_callback_snapshot()
    {
        var site = new FireSite();
        using var mission = World(site);
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        {
            HitPoints = 1000, Length = 10, Mass = 1, Speed = 100, MaxEnergy = 100,
            MaxFixedWeapons = 2, MaxWeapons = 5, SideMultiplier = 1, BackMultiplier = 1,
            MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1]
        };
        var carrier = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { Controls = new(0, 0, 0, -1), StateM = ShipControlState.CoastButton | ShipControlState.AllWeapons | 1 };
        mission.PlaceShip(carrier, mission.Sectors[0]);
        Weapon Mount(short id, short mount)
        {
            ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[id] =
                DataProjectileTypeIGC.Decode(new byte[DataProjectileTypeIGC.Size]) with
                { ProjectileTypeId = id, Speed = 100, Lifespan = 1, Radius = 1, Power = 1 };
            return carrier.MountWeapon(new(0, .5f, 1, 0, 0, id, 0, 0, 0), mount);
        }
        Mount(1, 4); Mount(2, 0); Mount(3, 2); var moved = Mount(4, 2); Mount(5, 0); Mount(6, 1);
        bool changed = false;
        site.OnShot = id =>
        {
            if (id != 3 || changed) return;
            changed = true;
            Mount(7, 2);
            moved.SetMountId(4);
        };
        carrier.Update(.6);
        Assert.Equal(new short[] { 6, 2, 3, 4, 1 }, site.Order);
        site.Order.Clear();
        carrier.Update(1.2);
        Assert.Equal(new short[] { 6, 2, 3, 7, 1, 4 }, site.Order);
        site.Order.Clear();
        carrier.Energy = 1; // only the selected fixed gun may spend this last shot
        carrier.Update(1.8);
        Assert.Equal(new short[] { 6 }, site.Order);
    }
    [Fact]
    public void Turret_prediction_does_not_mutate_previously_published_orientation_values()
    {
        using var mission = World();
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        {
            HitPoints = 1000, Length = 10, Mass = 1, Speed = 100, MaxEnergy = 100,
            MaxFixedWeapons = 0, MaxWeapons = 1, SideMultiplier = 1, BackMultiplier = 1,
            MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1]
        };
        var carrier = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { Controls = new(0, 0, 0, -1) };
        mission.PlaceShip(carrier, mission.Sectors[0]);
        var gunner = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
            { ParentShip = carrier, Controls = new(.5f, .25f, .1f, 0) };
        mission.PlaceShip(gunner, mission.Sectors[0]);
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[0] =
            DataProjectileTypeIGC.Decode(new byte[DataProjectileTypeIGC.Size]);
        carrier.MountWeapon(new(0, 1, 0, 0, 0, 0, 0, 0, 0)).SetGunner(gunner);
        var old = gunner.Orientation;
        var oldBasis = (old.GetRight(), old.GetUp(), old.GetBackward());
        carrier.Update(.1);
        var first = gunner.Orientation;
        var firstBasis = (first.GetRight(), first.GetUp(), first.GetBackward());
        carrier.Update(.2);
        carrier.Update(.3);
        Assert.NotSame(old, first);
        Assert.NotSame(first, gunner.Orientation);
        Assert.Equal(oldBasis, (old.GetRight(), old.GetUp(), old.GetBackward()));
        Assert.Equal(firstBasis, (first.GetRight(), first.GetUp(), first.GetBackward()));
        Assert.NotEqual(oldBasis, firstBasis);
    }

}
