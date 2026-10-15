using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P650SimRulesTests
{
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static DataAsteroidIGC Rock(short id, float x, float ore = 100) =>
        DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = id, ClusterId = 0, Position = [x, 0, 0], Up = [0, 1, 0], Forward = [0, 0, -1],
            Rotation = [0, 0, 0, 1], Ore = ore, OreMax = 100, Radius = 10,
            AabmCapabilities = Asteroid.CapMineHe3, HitPoints = 100, Fraction = 255
        };
    private static Mission Domain(StaticCore? core = null, IIgcSite? site = null, params DataAsteroidIGC[] rocks)
    {
        core ??= Core();
        var map = MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], rocks);
        var m = Mission.Assemble(core, map, [new SideSpec(0, "A", core.Civilizations.Keys.First())], site ?? new NullIgcSite());
        m.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        m.Stage = MissionStage.Started;
        return m;
    }
    private static Ship Ship(Mission m, byte guns = 0)
    {
        var h = m.Core.HullTypes.Values.First() with
        {
            HabmCapabilities = 0, Mass = 100, Thrust = 1000, Speed = 100, MaxFuel = 1000,
            MaxEnergy = 100, RechargeRate = 0, HitPoints = 1000, Length = 10,
            MaxFixedWeapons = guns, MaxWeapons = guns, Hardpoints = []
        };
        var ship = new Ship(123, h, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        m.PlaceShip(ship, 0);
        return ship;
    }
    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "test", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0, Signature: 0, PartId: 32000,
        SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "", Type: subtype);

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Starting_money_payday_interval_and_income_follow_loaded_slots()
    {
        Require.Artwork();
        var c = Core(); c.Constants.FloatConstants[11] = 1234; c.Constants.FloatConstants[6] = 2; c.Constants.FloatConstants[30] = 137;
        var m = Domain(c);
        m.ApplyStartingMoneyAndInitialPayday(0);
        var side = m.Sides[0];
        long seed = (long)(1234 * (1 + side.Civilization.BonusMoney));
        Assert.Equal(seed, side.Money);
        Assert.Equal(2, m.NextPaydayTime);
        m.Update(1.99); Assert.Equal(seed, side.Money);
        m.Update(2.01);
        Assert.Equal(seed + (long)(137 * (1 + side.Civilization.IncomeMoney)), side.Money);
        Assert.Equal(4, m.NextPaydayTime);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0.000167f)]
    [InlineData(0.003f)]
    [InlineData(0f)]
    public void Drained_He3_rock_regrows_at_loaded_rate(float rate)
    {
        Require.Artwork();
        var c = Core(); c.Constants.FloatConstants[14] = rate;
        var m = Domain(c, rocks: [Rock(1, 500)]);
        var rock = m.Sectors[0].Mineable.Single(); rock.MineOre(100);
        m.Update(0.25);
        float first = 100 * rate * 0.125f;
        Assert.Equal(first + rate * 0.125f * (100 - first), rock.OreRemaining, 6);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Team_money_bucket_price_uses_loaded_win_money()
    {
        Require.Artwork();
        var c = Core(); c.Constants.FloatConstants[12] = 23456;
        var m = Domain(c);
        var bucket = m.Sides[0].Buckets.Single(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        Assert.Equal(23456, bucket.Price);
        Assert.Equal(23456, BucketCatalog.Describe(c, TechTreeBitMask.Empty, GlobalAttributeSet.Identity).Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId).Price);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0, 2f)]
    [InlineData(1, 200f)]
    [InlineData(2, 4f)]
    public void Bounds_damage_reads_lens_radius_and_OOB_slots(int slot, float value)
    {
        Require.Artwork();
        var c = Core(); var fc = c.Constants.FloatConstants;
        fc[0] = 1; fc[1] = 100; fc[2] = 1; fc[slot] = value;
        var m = Domain(c); var s = Ship(m); s.Position = new Vector3(0, 0, 300);
        s.Controls = s.Controls with { Throttle = -1 };
        float bound = fc[2] * fc[1] * fc[1];
        float expected = 1 - 0.25f * (90000 / (fc[0] * fc[0]) - bound) / (50 * bound);
        s.Update(0.25);
        Assert.Equal(expected, s.Fraction, 6);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0.1f, false)]
    [InlineData(0.5f, true)]
    public void Shield_absorption_reads_loaded_downed_threshold(float threshold, bool leaks)
    {
        Require.Artwork();
        var c = Core(); c.Constants.FloatConstants[5] = threshold; c.Constants.DamageConstants[0][0] = 1;
        var m = Domain(c); var s = Ship(m);
        s.MountPart(Part(new DataPartTypeIGC.Shield(0, 1000, 0, 0, 0)), 1, 0, 255);
        s.SetShieldFraction(0.3f);
        s.ReceiveDamage(0, 1, 0, Vector3.Zero, Vector3.Zero, null);
        Assert.Equal(leaks, s.Fraction < 1);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Afterburner_uses_GA_thrust_and_current_step_power_without_scaling_fuel()
    {
        Require.Artwork();
        var m = Domain(); var s = Ship(m);
        float[] ga = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray(); ga[GlobalAttributeSet.Thrust] = 2;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(ga);
        s.MountPart(Part(new DataPartTypeIGC.Afterburner(0.01f, 1000, 2, 1, 0, 0)), 1, 0, 255);
        s.StateM = ShipControlState.AfterburnerButton; s.Controls = s.Controls with { Throttle = 1 };
        var input = new ShipKinematics.Inputs(s.Controls, s.StateM, s.EffectiveThrust(), s.EffectiveMaxSpeed(), s.Mass,
            s.HullType.SideMultiplier, s.HullType.BackMultiplier, s.EffectiveMaxTurnRate(0), s.EffectiveMaxTurnRate(1), s.EffectiveMaxTurnRate(2),
            s.EffectiveTurnTorque(0), s.EffectiveTurnTorque(1), s.EffectiveTurnTorque(2), 2000, 0.2f, false);
        var expected = Vector3.Zero;
        ShipKinematics.Step(in input, ref expected, Orientation.Identity, new float[3], 0.1f);
        s.Update(0.1);
        Assert.Equal(expected, s.Velocity);
        // Native runs the ramp again in the fuel-consuming pass: power=.4.
        Assert.Equal(1000 - 0.4f * 0.01f * 1000 * 0.1f, s.Fuel, 4);
        s.RepairAndRefuel(m.Core.StationTypes.Values.First());
        s.StateM = 0; s.Velocity = Vector3.Zero; s.Controls = s.Controls with { Throttle = -1 };
        s.ExecuteMove(0.1, 0.11);
        Assert.Equal(Vector3.Zero, s.Velocity); // resupply must remove the residual power tail
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Afterburner_mount_reaches_full_even_with_sub_byte_time_steps()
    {
        Require.Artwork();
        var m = Domain(); m.Core.Constants.FloatConstants[10] = 0.5f;
        var s = Ship(m);
        s.MountPart(Part(new DataPartTypeIGC.Afterburner(0, 1000, 10, 10, 0, 0)), 1, 0, 239);
        s.Controls = s.Controls with { Throttle = -1 };
        for (int i = 1; i <= 10; i++) s.Update(i * 0.001);
        Assert.Equal(1f, s.Parts.Single().MountedFraction);
        Assert.Equal(ExpandedPartData.FullyMounted, s.Parts.Single().FractionMounted);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(2, 2)]
    public void Selected_gun_gets_first_energy_then_descending_cyclic_mounts(int selected, int first)
    {
        Require.Artwork();
        var m = Domain(); var s = Ship(m, 3);
        var projectileIds = m.Core.ProjectileTypes.Keys.Take(3).ToArray();
        foreach (short mount in new short[] { 2, 0, 1 })
            s.MountWeapon(new DataPartTypeIGC.Weapon(0, 1, 10, 0, 0, projectileIds[mount], 0, 0, 0), mount);
        // Two shots' worth demonstrates both selected-first AND descending cycle.
        s.Energy = 20; s.StateM = ShipControlState.AllWeapons | selected;
        s.Update(0.01); // activation arms at now; fire on the next update
        s.Update(1.01);
        Assert.True(s.Weapons.Single(w => w.MountId == first).Active);
        var shots = m.Sectors[0].Models.OfType<Projectile>().ToList();
        Assert.Equal(2, shots.Count);
        Assert.Equal(new[] { projectileIds[first], projectileIds[(first + 2) % 3] },
            shots.Select(p => p.ProjectileType.ProjectileTypeId));
    }
    private sealed class FiringSite : IIgcSite
    {
        public Ship? Ship;
        public readonly List<float> ShieldAtShot = [];
        public void ProjectileCreatedEvent(Projectile projectile) => ShieldAtShot.Add(Ship!.ShieldFraction);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Guns_update_before_shield_regeneration()
    {
        Require.Artwork();
        var site = new FiringSite(); var m = Domain(site: site); var ship = Ship(m, 1); site.Ship = ship;
        ship.MountPart(Part(new DataPartTypeIGC.Shield(100, 1000, 0, 0, 0)), 1, 0, 255);
        ship.SetShieldFraction(0);
        ship.Ammo = 0;
        ship.MountWeapon(new DataPartTypeIGC.Weapon(0, 1, 1, 0, 0, m.Core.ProjectileTypes.Keys.First(), 0, 0, 0));
        ship.StateM = ShipControlState.AllWeapons;
        ship.Update(0.01);
        float before = ship.ShieldFraction;
        ship.Update(1.01);
        Assert.Single(site.ShieldAtShot);
        Assert.Equal(before, site.ShieldAtShot[0]);
        Assert.True(ship.ShieldFraction > before);
    }

}
