using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

public sealed class P747ConstructionTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private static (Mission Mission, Host Host, Asteroid Rock, Ship Builder, DataStationTypeIGC Type) World()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var host = new Host();
        var map = MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []);
        var mission = Mission.Assemble(core, map, Enumerable.Range(0, 3)
            .Select(i => new SideSpec((short)i, $"Side {i}", core.Civilizations.Keys.First())).ToArray(), host);
        mission.Stage = MissionStage.Started;
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        var type = core.StationTypes.Values.First() with { SuccessorStationTypeId = -1, Radius = 100, Signature = .01f };
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = 900, ClusterId = 0, Position = [1000, 0, 0], Rotation = [0, 0, 1, 0],
            Radius = 50, Signature = 1, HitPoints = 1000, Ore = 100, OreMax = 100,
            AabmCapabilities = (short)(type.AabmBuild | Asteroid.CapMineHe3)
        });
        mission.PlaceAsteroid(rock, mission.Sectors[0]);
        var hull = core.HullTypes.Values.First() with { Length = 10, ScannerRange = 100, Signature = 1, MaxAmmo = 100, MaxFuel = 100 };
        var builder = new Ship(901, hull, new Vector3(1000, 0, 200), Vector3.Zero, Orientation.Identity)
            { SideId = 0, BuilderStationType = type };
        mission.PlaceShip(builder, 0);
        return (mission, host, rock, builder, type);
    }

    // Igc/buildingeffectigc.h:160-225. Exercise the sector producer, including fade after transformation.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0, 1, .5f, 1)] [InlineData(5, 25.5f, .5f, 1)]
    [InlineData(10, 50, .5f, 1)] [InlineData(15, 80, .5f, 1)]
    [InlineData(20, 110, .5f, 1)] [InlineData(25, 110, .75f, 1)]
    [InlineData(30, 110, 1, 1)] [InlineData(35, 105, .5f, .5f)]
    public void Sector_advances_native_envelope_grow_opaque_and_shrink_phases(double seconds, float radius, float alpha, float intensity)
    {
        Require.Artwork(); var w = World();
        var effect = w.Mission.Sectors[0].StartBuildingEffect(w.Builder, w.Rock, w.Type, 1);
        w.Mission.Update(1 + seconds);
        Assert.Equal(radius, effect.Radius, 3); Assert.Equal(alpha, effect.InnerAlpha, 3);
        Assert.Equal(intensity, effect.InnerIntensity, 3); Assert.Equal(intensity, effect.OuterIntensity, 3);
        var expected = seconds < 10 ? effect.PositionStart * (1 - (float)seconds / 10) + effect.PositionStop * ((float)seconds / 10) : effect.PositionStop;
        Assert.InRange((effect.Position - expected).Length(), 0, .001f);
        Assert.Equal(seconds >= 30, effect.ConstructionCompleted); Assert.False(effect.IsTerminated);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Effect_transforms_at_thirty_and_expires_at_forty_without_an_interruption_receipt()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        var effect = sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0);
        w.Mission.Update(29.999); Assert.Null(w.Rock.ConstructedStation);
        w.Mission.Update(30); Assert.NotNull(w.Rock.ConstructedStation); Assert.False(effect.IsTerminated);
        Assert.Empty(sector.BuildingEffects); Assert.Null(w.Rock.BuildingEffect);
        w.Mission.Update(39.999); Assert.False(effect.IsTerminated);
        w.Mission.Update(40); Assert.True(effect.IsTerminated);
        Assert.Empty(sector.DrainBuildingEffectInterruptions()); Assert.Single(sector.StationRuntimes);
    }

    // FedSrv/FedSrv.CPP:11373-11462; buildingeffectigc.h:328-351.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Completion_callback_consumes_builder_before_station_creation_and_preserves_rock_health()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        int damageType = Array.FindIndex(w.Mission.Core.Constants.DamageConstants, row => row[Asteroid.DefenseType] > 0);
        float factor = w.Mission.Core.Constants.DamageConstants[damageType][Asteroid.DefenseType];
        w.Rock.ReceiveDamage((byte)damageType, 250 / factor, 0, Vector3.Zero, Vector3.Zero, null);
        float fraction = w.Rock.Fraction; Assert.InRange(fraction, .001f, .999f);
        bool observed = false;
        w.Host.StationTypeChanged += station =>
        {
            observed = true;
            Assert.DoesNotContain(w.Builder, sector.Models); Assert.Empty(w.Builder.Parts);
            Assert.Equal(0, w.Builder.Ammo); Assert.Equal(0, w.Builder.Fuel); Assert.Equal(0, w.Builder.StateM);
            Assert.Contains(w.Rock, sector.Mineable); // native creates station before terminating the asteroid
            Assert.Equal(fraction, station.ArmorFraction); Assert.Equal(0, station.ShieldFraction);
            Assert.Equal((byte)180, station.Data.BpHull);
            Assert.Equal(new float[] { 0, 0, 1, 0 }, station.Data.Rotation);
        };
        sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0); w.Mission.Update(30);
        Assert.True(observed); Assert.Empty(sector.Mineable); Assert.Empty(sector.Asteroids);
        Assert.Same(w.Rock.ConstructedStation, Assert.Single(sector.StationRuntimes));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Concrete_host_CreateBuildingEffect_and_BuildStation_execute_the_native_contract()
    {
        Require.Artwork(); var w = World(); IIgcSite site = w.Host; var sector = w.Mission.Sectors[0];
        site.CreateBuildingEffect(0, w.Rock, w.Builder);
        var effect = Assert.Single(sector.BuildingEffects); Assert.Same(effect, w.Rock.BuildingEffect);
        site.CreateBuildingEffect(0, w.Rock, w.Builder); Assert.Single(sector.BuildingEffects);
        Assert.Contains((short)0, effect.InformedSides); Assert.DoesNotContain((short)1, effect.InformedSides);
        w.Mission.Update(30); Assert.Single(sector.StationRuntimes);
        var next = new Asteroid(w.Rock.Data with { AsteroidId = 999, Position = [5000, 0, 0] });
        w.Mission.PlaceAsteroid(next, sector);
        site.BuildStation(next, w.Mission.Sides[0], w.Type, 31, new bool[6]);
        Assert.Equal(2, sector.StationRuntimes.Count); Assert.DoesNotContain(next, sector.Mineable);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Explicit_or_builder_death_interruption_unlinks_the_effect_once_and_keeps_the_rock(bool death)
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        var effect = sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0);
        if (death) { w.Builder.SetFraction(0); w.Mission.Update(.1); }
        else ((IIgcSite)w.Host).KillBuildingEffectEvent(effect);
        ((IIgcSite)w.Host).KillBuildingEffectEvent(effect);
        Assert.True(effect.IsTerminated); Assert.Null(w.Rock.BuildingEffect); Assert.False(w.Builder.IsBuilding);
        Assert.Empty(sector.DrainBuildingEffectCreations());
        Assert.Equal(new short[] { 900 }, sector.DrainBuildingEffectInterruptions());
        w.Mission.Update(50); Assert.Empty(sector.BuildingEffects); Assert.Empty(sector.StationRuntimes);
        Assert.Contains(w.Rock, sector.Mineable); Assert.Empty(sector.DrainBuildingEffectInterruptions());
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Asteroid_kill_callback_removes_runtime_and_export_record_and_cancels_attached_construction()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        var effect = sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0);
        bool notified = false; w.Host.AsteroidKilled = (rock, explode) => { Assert.Same(w.Rock, rock); Assert.True(explode); notified = true; };
        ((IIgcSite)w.Host).KillAsteroidEvent(w.Rock, true);
        Assert.True(notified); Assert.Empty(sector.Mineable); Assert.Empty(sector.Asteroids);
        Assert.True(effect.IsTerminated); Assert.Empty(sector.BuildingEffects);
        w.Mission.Update(50); Assert.Empty(sector.StationRuntimes);
    }

    // FedSrv/FedSrv.CPP:11404-11428: overlap is strict and zero separation is exempt.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0, false)] [InlineData(101, true)] [InlineData(102, false)] [InlineData(103, false)]
    public void Building_station_shortens_only_strictly_overlapping_nonconcentric_probe_lifetimes(float offset, bool expires)
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        var probeType = w.Mission.Core.ProbeTypes.Values.First() with { Radius = 2, Lifespan = 1000, Ammo = 0 };
        var probe = new Probe(950, sector, probeType, new Vector3(1000 + offset, 0, 0), 0, 0);
        sector.Add(probe);
        ((IIgcSite)w.Host).BuildStation(w.Rock, w.Mission.Sides[0], w.Type, 10, new bool[6]);
        Assert.Equal(expires ? 11 : 1000, probe.TimeExpire);
    }

    // Native Update uses the side's CURRENT eye and a strict >3 wall-clock inhibit boundary.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Ore_memory_updates_only_current_eyes_after_the_strict_wall_clock_inhibit_window()
    {
        Require.Artwork(); var w = World(); var clock = new Clock(); w.Rock.OreClock = clock;
        w.Builder.Position = new Vector3(1000, 0, 140);
        Assert.Equal(0, w.Rock.GetOreSeenBySide(0));
        w.Rock.SideVisibility.SetSideVisibility(0, true);
        w.Rock.SetInhibitUpdate(true); w.Mission.Update(.1);
        clock.Milliseconds = 3000; w.Mission.Update(.2); Assert.Equal(0, w.Rock.GetOreSeenBySide(0));
        clock.Milliseconds = 3001; w.Mission.Update(.3);
        Assert.Equal(100, w.Rock.GetOreSeenBySide(0)); Assert.Equal(0, w.Rock.GetOreSeenBySide(1));
        w.Rock.SideVisibility.SetSideVisibility(0, false); w.Rock.MineOre(25);
        w.Mission.Update(.4); Assert.Equal(100, w.Rock.GetOreSeenBySide(0));
        w.Rock.SideVisibility.SetSideVisibility(1, true); w.Mission.Update(.5);
        Assert.Equal(w.Rock.OreRemaining, w.Rock.GetOreSeenBySide(1));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Authoritative_ore_fraction_updates_bypass_inhibit_and_can_refresh_every_side()
    {
        Require.Artwork(); var w = World(); w.Rock.SetInhibitUpdate(true);
        w.Rock.SideVisibility.SetSideVisibility(0, true);
        w.Rock.SetOreWithFraction(.5f, false);
        Assert.Equal(50, w.Rock.GetOreSeenBySide(0)); Assert.Equal(0, w.Rock.GetOreSeenBySide(1));
        w.Rock.SetOreWithFraction(.25f, true);
        Assert.All(w.Mission.Sides, side => Assert.Equal(25, w.Rock.GetOreSeenBySide(side.Id)));
        Assert.True(w.Rock.InhibitOreUpdate);
    }

    // Igc/clusterIGC.cpp:141-155: source rock visibility differs from a tiny-signature station.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Stale_rock_discovery_is_side_scoped_ship_only_and_emitted_once()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0); w.Mission.Update(30);
        var station = w.Rock.ConstructedStation!; Assert.Contains((short)1, station.StaleRockSides);
        var scout = new Ship(902, w.Builder.HullType, new Vector3(1000, 0, 140), Vector3.Zero, Orientation.Identity) { SideId = 1, StationId = station.StationId };
        w.Mission.PlaceShip(scout, sector);
        var notices = new List<(short Rock, short Sector, short Side)>();
        w.Host.StaleAsteroidKilled = (rock, cluster, side) => notices.Add((rock, cluster, side.Id));
        w.Mission.Update(30.01); Assert.Empty(notices); // a docked scanner cannot discover the ghost
        scout.StationId = null;
        w.Mission.Update(30.02); Assert.Equal(new[] { ((short)900, (short)0, (short)1) }, notices);
        w.Mission.Update(30.03); Assert.Single(notices); Assert.Contains((short)2, station.StaleRockSides);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Destroying_an_unseen_constructed_station_retires_its_rock_for_uninformed_sides()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        sector.StartBuildingEffect(w.Builder, w.Rock, w.Type, 0); w.Mission.Update(30);
        var station = w.Rock.ConstructedStation!;
        var notices = new List<short>(); w.Host.StaleAsteroidKilled = (_, _, side) => notices.Add(side.Id);
        ((IIgcSite)w.Host).KillStationEvent(station, w.Builder, 1);
        Assert.Equal(new short[] { 1, 2 }, notices.Order().ToArray()); Assert.Empty(station.StaleRockSides);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Late_attached_asteroid_records_have_the_owner_needed_by_native_construction_callbacks()
    {
        Require.Artwork(); var w = World(); var sector = w.Mission.Sectors[0];
        sector.AttachAsteroids([w.Rock.Data with { AsteroidId = 999 }]);
        var rock = sector.Mineable.Single(a => a.Data.AsteroidId == 999);
        Assert.Same(sector, rock.Sector); Assert.Same(w.Mission, rock.Mission);
        ((IIgcSite)w.Host).CreateBuildingEffect(0, rock, w.Builder);
        w.Mission.Update(30); Assert.NotNull(rock.ConstructedStation);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Concrete_cluster_callback_observes_completed_sector_chunks_and_no_repeated_clock()
    {
        Require.Artwork(); var w = World(); var ticks = new List<double>();
        w.Host.ClusterUpdated = sector => { Assert.Same(w.Mission.Sectors[0], sector); ticks.Add(sector.LastUpdate); };
        w.Mission.Update(.5); Assert.NotEmpty(ticks); Assert.Equal(.5, ticks.Last(), 6);
        int count = ticks.Count; w.Mission.Update(.5); Assert.Equal(count, ticks.Count);
    }
}
