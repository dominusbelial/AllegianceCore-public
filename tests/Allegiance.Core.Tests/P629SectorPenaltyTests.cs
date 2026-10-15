using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P629SectorPenaltyTests
{
    internal static Mission Domain(IIgcSite? site = null)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []);
        var m = Mission.Assemble(core, map, [new SideSpec(0, "A", core.Civilizations.Keys.First())], site ?? new NullIgcSite());
        m.Sides[0].GlobalAttributes = GlobalAttributeSet.Identity;
        m.Stage = MissionStage.Started;
        return m;
    }

    internal static Ship Add(Mission m, short id, byte pilot = Ship.PilotTypePlayer, bool pod = false, Ship? parent = null)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            HabmCapabilities = pod ? DataHullTypeIGC.HabmLifepod : (short)0,
            HitPoints = 1000, RechargeRate = 0, Thrust = 0, Length = 1,
            MaxWeapons = 0, MaxFixedWeapons = 0, Hardpoints = []
        };
        m.Core.Constants.DamageConstants[0][hull.DefenseType] = 1;
        var ship = new Ship(id, hull, new Vector3((id % 20 - 10) * 100, (id / 20 - 10) * 100, 0), Vector3.Zero, Orientation.Identity)
        { SideId = 0, PilotType = pilot, ParentShip = parent };
        m.PlaceShip(ship, 0);
        return ship;
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(71, 0f)]
    [InlineData(72, 0f)]
    [InlineData(73, 0.00025f)]
    public void Loaded_core_threshold_and_collision_damage(int players, float lost)
    {
        Require.Artwork();
        var m = Domain();
        Assert.Equal(-72f, m.Core.Constants.FloatConstants[37]);
        for (short i = 0; i < players; i++) Add(m, i);
        var first = m.Sectors[0].Models.OfType<Ship>().First();
        m.Sectors[0].Update(0.1);
        Assert.Equal(1f - lost, first.Fraction, 6);
        Assert.Equal(players > 72 ? ShipWarningMask.CrowdedSector : ShipWarningMask.None, first.WarningMask);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(Ship.PilotTypePlayer, false, false, 7f)]
    [InlineData(Ship.PilotTypePlayer, true, false, 3f)]
    [InlineData(Ship.PilotTypePlayer, true, true, 5f)]
    [InlineData(Ship.PilotTypeMiner, true, true, 2f)]
    public void Loaded_cost_weights_use_native_drone_parent_pod_player_precedence(byte pilot, bool pod, bool parent, float expected)
    {
        Require.Artwork(); var m = Domain(); var fc = m.Core.Constants.FloatConstants;
        fc[33] = 3; fc[34] = 5; fc[35] = 2; fc[36] = 7; fc[37] = 0; fc[38] = 1;
        var ship = Add(m, 1, pilot, pod); if (parent) ship.ParentShip = ship;
        m.Sectors[0].Update(1);
        Assert.Equal(expected, m.Sectors[0].Cost);
        // Drone costs contribute, but drones and boarded occupants never take crowding damage.
        if (pilot < Ship.PilotTypePlayer || parent) Assert.Equal(1f, ship.Fraction);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0.05)] [InlineData(0.25)] [InlineData(1.0)]
    public void Cost_scales_once_by_chunk_dt_and_loaded_divisor(double dt)
    {
        Require.Artwork(); var m = Domain(); var fc = m.Core.Constants.FloatConstants;
        fc[37] = -1; fc[36] = 3; fc[38] = 20;
        var ship = Add(m, 1);
        m.Sectors[0].Update(dt);
        Assert.Equal(2f * (float)dt / 20f, m.Sectors[0].Cost, 6);
        Assert.Equal(1f - 2f * (float)dt / 20f, ship.Fraction, 6);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Warning_clears_and_inactive_or_off_cost_resets()
    {
        Require.Artwork(); var m = Domain(); var fc = m.Core.Constants.FloatConstants;
        fc[37] = 0; var ship = Add(m, 1); var sector = m.Sectors[0];
        sector.Update(0.1); Assert.Equal(ShipWarningMask.CrowdedSector, ship.WarningMask);
        fc[37] = -fc[36]; sector.Update(0.2);
        Assert.Equal(0, sector.Cost); Assert.Equal(ShipWarningMask.None, ship.WarningMask);
        fc[37] = -100; sector.Update(0.3); Assert.Equal(-99f, sector.Cost); // no scaling of nonpositive costs
        fc[37] = 0; m.Stage = MissionStage.NotStarted; sector.Update(0.4);
        Assert.Equal(0, sector.Cost); Assert.Equal(ShipWarningMask.None, ship.WarningMask);
        m.Stage = MissionStage.Started; m.CrowdingPenaltyEnabled = false;
        float before = ship.Fraction; sector.Update(0.5);
        Assert.Equal(0, sector.Cost); Assert.Equal(before, ship.Fraction);
        Assert.Equal(ShipWarningMask.None, ship.WarningMask);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Installed_shield_maximum_and_side_attributes_feed_normal_collision_damage()
    {
        Require.Artwork(); var site = new DamageSite(); var m = Domain(site);
        m.Core.Constants.FloatConstants[37] = 0; m.Core.Constants.FloatConstants[38] = 10;
        var s = Add(m, 1);
        var shield = m.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Shield);
        shield = shield with { Type = new DataPartTypeIGC.Shield(0, 500, s.HullType.DefenseType, 0, 0) };
        var attributes = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        attributes[GlobalAttributeSet.MaxArmorShip] = 2; attributes[GlobalAttributeSet.MaxShieldShip] = 3;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(attributes);
        s.MountPart(shield, 1, 0, 255); s.SetShieldFraction(0); // down: full damage leaks to hull
        float maximum = s.EffectiveMaxHitPoints() + s.EffectiveMaxShieldStrength((DataPartTypeIGC.Shield)shield.Type);
        m.Sectors[0].Update(0.1);
        var damage = Assert.Single(site.Hits);
        Assert.Equal((byte)0, damage.Type); Assert.Equal(0.01f * maximum, damage.Amount, 4);
        Assert.Null(damage.Launcher);
        Assert.Equal(1f - 0.01f * maximum / s.EffectiveMaxHitPoints(), s.Fraction, 5);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(1f)] [InlineData(1.21f)] [InlineData(2.5f)]
    public void Oob_uses_loaded_ellipsoid_and_bypasses_shields_even_with_crowding_off(float bound)
    {
        Require.Artwork(); var m = Domain(); var fc = m.Core.Constants.FloatConstants;
        fc[0] = 0.5f; fc[1] = 1000; fc[2] = bound; m.CrowdingPenaltyEnabled = false;
        var s = Add(m, 1); var sector = m.Sectors[0];
        s.MountPart(m.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Shield), 1, 0, 255);
        s.SetShieldFraction(1);
        s.Position = new Vector3(0, 0, 500 * MathF.Sqrt(bound));
        sector.Update(0.1); Assert.Equal(1, s.Fraction); Assert.Equal(ShipWarningMask.None, s.WarningMask);
        s.Position = new Vector3(0, 0, 1000 * MathF.Sqrt(bound)); // r² = 4 * boundary²
        sector.Update(0.2);
        Assert.Equal(1 - 0.1f * 3 / 50, s.Fraction, 6);
        Assert.Equal(1, s.ShieldFraction);
        Assert.Equal(ShipWarningMask.OutOfBounds, s.WarningMask);
        s.Position = Vector3.Zero; sector.Update(0.3); Assert.Equal(ShipWarningMask.None, s.WarningMask);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Docked_ships_do_not_contribute_or_take_environmental_damage()
    {
        Require.Artwork(); var m = Domain(); m.Core.Constants.FloatConstants[37] = 0;
        var s = Add(m, 1); s.StationId = 123; s.Position = new(1e6f, 0, 0);
        m.Sectors[0].Update(0.1);
        Assert.Equal(0, m.Sectors[0].Cost); Assert.Equal(1, s.Fraction); Assert.Equal(ShipWarningMask.None, s.WarningMask);
    }

    private sealed class DamageSite : IIgcSite
    {
        public readonly List<(byte Type, float Amount, IModelIgc? Launcher)> Hits = [];
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher, byte type,
            float amount, float leakage, Vector3 p1, Vector3 p2) => Hits.Add((type, amount, launcher));
    }
}
