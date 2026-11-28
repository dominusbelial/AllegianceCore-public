using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Envelope = Allegiance.Tools.McpSpawner.StrikeWeaponEnvelope;

namespace Allegiance.ClientCore.Tests;

public sealed class P1105StrikeEnvelopeTests
{
    private static readonly MissileLockState Station = new(1, 1, Vector3.Zero, "mag", IgcObjectType.Station);

    [SkippableFact]
    public void Guided_grant_uses_accelerated_missile_range_and_station_lock()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        // A lightly defended target accepts this real seeker's damage type.
        var profile = StrikeVolley.Profile(core, 315)!.Value;
        byte exposed = (byte)Array.FindIndex(profile.DamageConstants[4], value => value > 0);
        profile = profile with { ArmorDefense = exposed, ShieldDefense = exposed };
        var w = Envelope.Select(core, core.HullTypes[23], [new(102, 2, 0, 240)], profile,
            GlobalAttributeSet.Identity, 0, 0, 0);
        Assert.NotNull(w);
        Assert.Equal(Envelope.Kind.Guided, w.Value.Kind);
        Assert.Equal(1645, w.Value.Range);
        Assert.False(Envelope.Ready(w.Value, 1, 1645, 1, true, Station, false, true));
        Assert.False(Envelope.Ready(w.Value, 1, 1644, 1, true, Station with { LockFraction = .9f }, false, true));
        Assert.True(Envelope.Ready(w.Value, 1, 1644, 1, true, Station, false, true));
    }

    [SkippableFact]
    public void Dumbfire_uses_granted_rounds_range_bow_and_load_readiness()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var profile = StrikeVolley.Profile(core, 315)!.Value;
        var hull = core.HullTypes[23];
        ExpandedPartData[] parts = [new(126, 5, 255, 240), new(158, 2, 0, 240)];
        var w = Envelope.Select(core, hull, parts, profile, GlobalAttributeSet.Identity, 1, 0, 0);
        Assert.NotNull(w);
        Assert.Equal(Envelope.Kind.Dumbfire, w.Value.Kind);
        Assert.Equal(926.875f, w.Value.Range);
        Assert.False(Envelope.Ready(w.Value, 1, 950, 1, true, Station, false, true));
        var feedback = Station with { LockFraction = 0 };
        Assert.True(Envelope.Ready(w.Value, 1, 925, 1, true, feedback, false, true));
        Assert.False(Envelope.Ready(w.Value, 1, 925, .5f, true, feedback, false, true));
        Assert.False(Envelope.Ready(w.Value, 1, 925, 1, true, feedback with { IsReady = false }, false, true));
        Assert.Null(Envelope.Select(core, hull, parts, profile, GlobalAttributeSet.Identity, 2, 0, 0));
    }

    [SkippableFact]
    public void Gun_uses_fixed_granted_mount_with_side_ballistics_and_available_pools()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var hull = core.HullTypes[23];
        var profile = StrikeVolley.Profile(core, 315)!.Value;
        var factors = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        factors[GlobalAttributeSet.LifespanEnergy] = 1.5f;
        factors[GlobalAttributeSet.SpeedAmmo] = 2;
        var ga = GlobalAttributeSet.FromArray(factors);
        ExpandedPartData[] parts = [new(74, 1, 0, 240), new(177, 1, 255, 240)];
        var w = Envelope.Select(core, hull, parts, profile, ga, 0, 1000, 1000);
        Assert.NotNull(w);
        Assert.Equal(Envelope.Kind.Gun, w.Value.Kind);
        Assert.Equal(74, w.Value.Part);
        var grantedGun = Assert.IsType<DataPartTypeIGC.Weapon>(core.PartTypes[74].Type);
        Assert.Equal(grantedGun.DtimeBurst, w.Value.Cycle);
        Assert.Equal(675 * 1.5f, w.Value.Range);
        Assert.Null(Envelope.Select(core, hull, parts, profile, ga, 0, 0, 0));
        Assert.False(Envelope.Ready(w.Value, 1, 500, 1, true, default, false, true));
        Assert.True(Envelope.Ready(w.Value, 1, 500, 1, true, default, true, true));
    }

    [Fact]
    public void Ship_with_same_numeric_id_and_different_station_both_block_release()
    {
        var w = new Envelope.Weapon(Envelope.Kind.Guided, 102, 0, 1645, 4);
        Assert.False(Envelope.Ready(w, 1, 800, 1, true, Station with { TargetType = IgcObjectType.Ship }, false, true));
        Assert.False(Envelope.Ready(w, 1, 800, 1, true, Station with { TargetId = 2 }, false, true));
        Assert.False(Envelope.Ready(w, 1, 800, 1, false, Station, false, true));
    }

    [Fact]
    public void Cloak_lane_applies_lateral_thrust_to_final_controls()
    {
        var visible = Envelope.Lane(new Vector3(0, 0, 1), false, 1000);
        var cloak = Envelope.Lane(new Vector3(0, 0, 1), true, 1000);
        Assert.Equal(850f, cloak.Length(), 2);
        Assert.Equal(0, Envelope.Movement(visible, Vector3.Zero, new Orientation(), visible));
        var thrust = Envelope.Movement(visible, Vector3.Zero, new Orientation(), cloak);
        Assert.NotEqual(0, thrust & (ShipControlState.LeftButton | ShipControlState.RightButton));
    }

    [Fact]
    public void P1105b_Delivery_at_the_required_round_boundary_releases_only_the_ready_subset()
    {
        var profile = new StrikeVolley.StationProfile(35, 40, 0, 0, 0, 0, [[1]]);
        StrikeVolley.Loadout loadout = new(23, [new(0, 10, 8, 1)]);
        var ready = new List<short> { 2, 4 };
        var arrived = new List<short> { 1, 2, 3, 4 };
        var wait = new Envelope.ReadinessWait();
        string? reason = null;
        Assert.True(Envelope.Sufficient(profile, [loadout, loadout], .5, 8));
        Assert.False(Envelope.Sufficient(profile, [loadout, loadout], .499, 8));
        Assert.False(Envelope.Sufficient(profile, [loadout, loadout], .5, 9));
        Assert.False(Envelope.Sufficient(profile with { MaxArmor = 400 }, [loadout, loadout], .5, 8));
        Assert.Null(wait.Members(arrived, ready, true, 100, ref reason));
        reason = "massed";
        Assert.Same(ready, wait.Members(arrived, ready, true, 101, ref reason));
        Assert.Equal("weapon-ready", reason);
    }

    [Fact]
    public void Only_an_accepted_owner_launch_after_permission_spends_the_member()
    {
        Assert.False(Envelope.Spent(null, 12));
        Assert.False(Envelope.Spent(12, 12));
        Assert.False(Envelope.Spent(12, 0));
        Assert.True(Envelope.Spent(12, 13));
    }


}

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void P1105_Gun_only_station_order_requires_the_owner_clear_and_acquire_edges()
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, WeaponEnvelope: true));
        rig.Mature();
        var bot = rig.Bots[0];
        var cs = rig.Sessions[0];
        var station = rig.Stations[1];
        var p = station.Position;
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = 23 });
        cs.World.Apply(new SetCluster { SectorId = station.ClusterId, Ship = ServerShipUpdate.FromValues(1,
            0, default, 1, new(p[0], p[1], p[2] + 500), Orientation.Identity, Vector3.Zero,
            [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, [new(74, 1, 0, 240)]));
        Set(bot, "HullId", (short)23);
        Set(bot, "FinaleBomberActive", true);
        var plan = Get<System.Collections.IDictionary>(Get<object>(bot, "Finale"), "Plans")[(short)0]!;
        var go = rig.Started + 1820;
        Set(plan, "GoTime", (double?)go);
        Set(plan, "WaveAcceptedRounds", 0L);
        Get<HashSet<short>>(plan, "GoMembers").Add(1);
        void Hold(double dt) => Call("HoldFinaleStrike", rig.Manager, bot, go + dt);
        void Pools(byte hull, byte shield) =>
            cs.World.Apply(new HeavyShipsUpdate { BpTargetHull = hull, BpTargetShield = shield });

        Pools(240, 240); // previous target
        Hold(-5);
        Pools(240, 255); // a ship without a shield is still a target
        Hold(-4);
        Assert.Null(cs.World.MyCurrentCommand);
        Pools(255, 255);
        Hold(-3);
        Assert.Equal(station.StationId, cs.World.MyCurrentCommand?.TargetId);
        Hold(0);
        Assert.False(Get<bool>(bot, "FinaleReleaseLogged"));
        Pools(240, 240);
        Hold(.1);
        Assert.True(Get<bool>(bot, "FinaleReleaseLogged"));
        Assert.False(Get<bool>(bot, "FinaleStrikeSpent"));
        cs.World.Apply(new ProjectileInstance { LauncherType = IgcObjectType.Ship, LauncherId = 1 });
        Hold(.2);
        Assert.True(Get<bool>(bot, "FinaleStrikeSpent"));
        Assert.Equal(1, Get<long>(plan, "WaveAcceptedRounds"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void P1105_Hold_checks_the_owner_again_at_go_and_waits_for_the_launch_echo(bool enabled)
    {
        Require.Artwork();
        using var reads = new DefenseLiveReads();
        using var rig = new FinaleRig(new(Bombers: 1, WeaponEnvelope: enabled));
        rig.Mature();
        var bot = rig.Bots[0];
        var cs = rig.Sessions[0];
        var station = rig.Stations[1];
        var p = station.Position;
        var position = new Vector3(p[0], p[1], p[2] + 800);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = 23 });
        cs.World.Apply(new SetCluster { SectorId = station.ClusterId, Ship = ServerShipUpdate.FromValues(1,
            0, default, 1, position, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        Set(bot, "Granted", new BotManager.LoadoutGrant(23, [new(158, 2, 0, 240)]));
        Set(bot, "HullId", (short)23);
        Set(bot, "FinaleBomberActive", true);
        Set(bot, "FinaleGrantVerified", true);
        var run = Get<object>(bot, "Finale");
        var plan = Get<System.Collections.IDictionary>(run, "Plans")[(short)0]!;
        double go = rig.Started + 1820;
        Set(plan, "GoTime", (double?)go);
        Set(plan, "WaveAcceptedRounds", 0L);
        Get<HashSet<short>>(plan, "GoMembers").Add(1);
        void Feedback(IgcObjectType type) => cs.World.Apply(MissileLockUpdate.From(1, station.StationId,
            1, Vector3.Zero, "ab", type));
        bool Hold(double at) => (bool)Call("HoldFinaleStrike", rig.Manager, bot, at)!;

        var sender = P700Sender(bot);
        Set(bot, "FinaleLoneWolf", true);
        Feedback(IgcObjectType.Ship);
        Assert.True(Hold(go - 1));
        int lateral = ShipControlState.LeftButton | ShipControlState.RightButton;
        Assert.Equal(enabled, (P700LastControls(sender).Payload.StateM & lateral) != 0);
        Assert.Equal(enabled, cs.World.MyCurrentCommand?.TargetId == station.StationId);
        Feedback(IgcObjectType.Ship);
        Assert.Equal(enabled, Hold(go));
        Assert.Equal(!enabled, Get<bool>(bot, "FinaleReleaseLogged"));
        Assert.Equal(!enabled, Get<bool>(bot, "FinaleStrikeSpent"));
        if (!enabled) return;
        Feedback(IgcObjectType.Station);
        Hold(go + .1);
        Assert.True(Get<bool>(bot, "FinaleReleaseLogged"));
        Assert.False(Get<bool>(bot, "FinaleStrikeSpent"));
        cs.World.Apply(new FireMissile { LauncherId = 2, Entries = [new(default, default, default, 991)] });
        Hold(go + .2);
        Assert.False(Get<bool>(bot, "FinaleStrikeSpent"));
        cs.World.Apply(new FireMissile { LauncherId = 1, Entries = [new(default, default, default, 992)] });
        Hold(go + .3);
        Hold(go + .4);
        Assert.True(Get<bool>(bot, "FinaleStrikeSpent"));
        Assert.Equal(1, Get<long>(plan, "WaveAcceptedRounds"));
    }
}
