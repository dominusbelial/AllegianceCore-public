using System.Buffers.Binary;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

// Synthetic loaded core and map: no artwork, native checkout, server or sockets.
public sealed class P649CombatBlastParityTests
{
    private sealed class Site : IIgcSite
    {
        public readonly List<byte> StationDamageTypes = [];
        public readonly List<(byte Type, double Time, Vector3 Target, Vector3 Blast)> ShipDamage = [];
        public int StationKills, AsteroidKills, MissileKills, ProbeKills, WarpBombs;
        public readonly List<short> LostRestartSides = [];
        public void SideLostRestartStation(Mission mission, short sideId) => LostRestartSides.Add(sideId);
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher,
            byte type, float amount, float leakage, Vector3 p1, Vector3 p2) => ShipDamage.Add((type, now, p1, p2));
        public void DamageStationEvent(IStationIgc station, IModelIgc launcher, byte type, float amount) => StationDamageTypes.Add(type);
        public void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount) => StationKills++;
        public void KillAsteroidEvent(IAsteroidIgc asteroid, bool explodeF) => AsteroidKills++;
        public void MissileDestroyedEvent(Missile missile) => MissileKills++;
        public void ProbeDestroyedEvent(Probe probe) => ProbeKills++;
        public void WarpBombEvent(IWarpIgc warp, IMissileIgc missile) => WarpBombs++;
    }

    private static DataHullTypeIGC Hull => DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
    {
        HitPoints = 100, Length = 10, DefenseType = 0, Mass = 1, Speed = 100, Thrust = 100, SideMultiplier = 1, BackMultiplier = 1,
        MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1]
    };
    private static DataMissileTypeIGC MissileType => DataMissileTypeIGC.Decode(new byte[DataMissileTypeIGC.Size]) with
    {
        HitPoints = 100, Radius = 5, DefenseType = 0, Lifespan = 100,
        ReadyTime = 100, DamageType = 3, Power = 20, BlastPower = 99, BlastRadius = 100
    };

    private static (Mission Mission, Site Site) Domain(float delay = 5, bool pairedWarps = false, bool reverseOrder = false)
    {
        var body = new byte[DataConstantsIGC.Size + DataProjectileTypeIGC.Size];
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(32 * 4), delay);
        for (int d = 0; d < 20; d++)
            for (int def = 0; def < 20; def++)
                BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(DataConstantsIGC.DamageOffset + 4 * (20 * d + def)), d == 3 ? 2 : 1);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(DataConstantsIGC.Size + 72), 1);
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize, Version = 1, Body = body,
            BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new IgcRecord(IgcObjectType.Constants, DataConstantsIGC.Size, 0),
                new IgcRecord(IgcObjectType.ProjectileType, DataProjectileTypeIGC.Size, DataConstantsIGC.Size)]
        });
        var a = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0, Name = "A" };
        var b = a with { ClusterId = 1, Name = "B" };
        var site = new Site();
        var rawWarp = DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
        { WarpId = 10, DestinationId = 11, ClusterId = 0, Radius = 5, Position = [1000, 0, 0] };
        var partner = rawWarp with { WarpId = 11, DestinationId = 10, ClusterId = 1, Position = [-1000, 0, 0] };
        var mission = Mission.Assemble(core, MissionMap.FromLists(reverseOrder ? [b, a] : [a, b], pairedWarps ? [rawWarp, partner] : [], [], []), [], site);
        mission.Stage = MissionStage.Started;
        return (mission, site);
    }

    private static Ship Ship(Mission m, Sector s, short id, short side, Vector3 position, Vector3? velocity = null)
    {
        var ship = new Ship(id, Hull, position, velocity ?? Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(ship, s);
        return ship;
    }

    private static Func<float> Target(string kind, Mission m, Sector s, Ship owner, Vector3 pos, bool armor = false)
    {
        switch (kind)
        {
            case "ship": return () => owner.Fraction;
            case "asteroid":
                var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
                { AsteroidId = 20, ClusterId = s.Id, Position = [pos.X, pos.Y, pos.Z], HitPoints = 100, Radius = 5 });
                m.PlaceAsteroid(rock, s);
                return () => rock.Fraction;
            case "station":
                var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
                { StationId = 21, ClusterId = s.Id, SideId = owner.SideId!.Value, Position = [pos.X, pos.Y, pos.Z] },
                    DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
                    { MaxArmorHitPoints = 100, MaxShieldHitPoints = 100, Radius = 5 });
                m.PlaceStation(station, s);
                return () => armor ? station.ArmorFraction : station.ShieldFraction;
            case "missile":
                var missile = new Missile(22, s, MissileType, pos, Vector3.Zero, new Vector3(0, 0, 1), 0, owner);
                s.Add(missile);
                return () => missile.Fraction;
            case "probe":
            case "armedProbe":
            case "rescueProbe":
                var probe = new Probe(23, s, DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]) with
                { HitPoints = 100, Radius = 5, Lifespan = 100, ProjectileTypeId = kind == "probe" ? (short)-1 : (short)1,
                    EabmCapabilities = kind == "rescueProbe" ? (short)0x4000 : (short)0 }, pos, 0, owner.SideId, owner);
                s.Add(probe);
                return () => probe.Fraction;
            default: throw new ArgumentException(kind);
        }
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("asteroid")]
    [InlineData("station")]
    [InlineData("missile")]
    [InlineData("probe")]
    public void Every_native_target_takes_surface_falloff_with_flagged_matrix_damage(string kind)
    {
        var (m, _) = Domain();
        var s = m.Sectors[0];
        var pos = new Vector3(55, 0, 0); // surface distance 50, f²=.25
        var owner = Ship(m, s, 1, 1, pos);
        var fraction = Target(kind, m, s, owner, pos);
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, null), 0);
        Assert.Equal(.9f, fraction(), 5); // 20 * .25 * matrix(3,0)=2 / 100
    }

    [Theory]
    [InlineData(-1, 0)] // sensor
    [InlineData(1, 0)] // armed: preserve the P100/P491 friendly-hit contract
    [InlineData(1, 0x4000)] // RescueAny
    public void Same_side_missile_damages_probe_with_friendly_fire_off(short projectileId, short capabilities)
    {
        var (m, _) = Domain();
        var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, new Vector3(1000, 0, 0));
        var probe = new Probe(23, s, DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]) with
        {
            HitPoints = 100, Radius = 5, Lifespan = 100,
            ProjectileTypeId = projectileId, EabmCapabilities = capabilities
        }, Vector3.Zero, 0, owner.SideId, owner);
        s.Add(probe);
        var missile = new Missile(24, s, MissileType with { ReadyTime = 0, BlastPower = 0 },
            new Vector3(0, 0, 100), new Vector3(0, 0, -100), new Vector3(0, 0, -1), 0, owner);
        s.Add(missile);

        for (int tick = 1; tick <= 30 && s.Models.Contains(missile); tick++) s.Update(tick * .05);

        Assert.False(m.AllowFriendlyFire);
        Assert.DoesNotContain(missile, s.Models);
        Assert.Equal(.6f, probe.Fraction, 5); // Power 20 * loaded matrix 2 / HP 100
        Assert.Contains(probe, s.Models);
    }

    [Fact]
    public void Launcherless_station_damage_uses_matrix_before_hostile_capture()
    {
        var (m, site) = Domain();
        var s = m.Sectors[0];
        m.Core.Constants.DamageConstants[0][0] = 0; // immune row, like the live fixture's type 0
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 21, ClusterId = s.Id, SideId = 2, Position = [0, 0, 0] },
            DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
            {
                MaxArmorHitPoints = 2000, MaxShieldHitPoints = 100, Radius = 40,
                SabmCapabilities = DataStationTypeIGC.SabmRestart | DataStationTypeIGC.SabmCapLand
            });
        m.PlaceStation(station, s);
        var capturer = new Ship(1, Hull with { HabmCapabilities = DataHullTypeIGC.HabmBoard },
            new Vector3(1000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        m.PlaceShip(capturer, s);

        // stationIGC.cpp:160-171: zero dtmShield means no damage, even with a NULL launcher.
        Assert.Equal(DamageResult.NoDamage, station.ReceiveDamage(0, station.EffectiveMaxShield(),
            0, Vector3.Zero, Vector3.Zero, null));
        Assert.Equal(1f, station.ShieldFraction);
        float dtm = m.Core.Constants.DamageConstants[3][station.Type.DefenseTypeShield];
        station.ReceiveDamage(3, station.EffectiveMaxShield() / dtm,
            0, Vector3.Zero, Vector3.Zero, null);
        Assert.Equal(0f, station.ShieldFraction, 5);
        Assert.Equal(1f, station.ArmorFraction, 5);

        capturer.Position = station.Center;
        s.Update(.05);

        Assert.Equal((short)1, station.SideId);
        Assert.Equal(.5f, station.ArmorFraction, 5); // FedSrv/fsship.cpp:582
        Assert.Equal(0f, station.ShieldFraction, 5); // zero regen: fsship.cpp:589-596
        Assert.Equal((short)2, Assert.Single(site.LostRestartSides));
        Assert.Contains(station, s.StationRuntimes);
        Assert.Equal(0, site.StationKills);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Ship_friendly_and_self_splash_obeys_the_mission_toggle(bool allow, bool self)
    {
        var (m, _) = Domain();
        m.AllowFriendlyFire = allow;
        var s = m.Sectors[0];
        var target = Ship(m, s, 1, 1, Vector3.Zero);
        var launcher = self ? target : Ship(m, s, 2, 1, new Vector3(1000, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(allow ? .6f : 1f, target.Fraction, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Friendly_and_self_repair_splash_heals_and_caps_at_one(bool self)
    {
        var (m, _) = Domain();
        var s = m.Sectors[0];
        var target = Ship(m, s, 1, 1, Vector3.Zero);
        var launcher = self ? target : Ship(m, s, 2, 1, new Vector3(1000, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, null), 0);
        s.ResolveExplosion(new Explosion(3, -10, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(.8f, target.Fraction, 5);
        s.ResolveExplosion(new Explosion(3, -100, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(1f, target.Fraction);
    }

    [Theory]
    [InlineData(0, 1)] // already integrated: rewind to explosion time
    [InlineData(1, 0)] // not integrated: extrapolate forward
    public void Moving_ship_is_sampled_at_the_explosion_timestamp(double time, double stateTime)
    {
        var (m, site) = Domain();
        var s = m.Sectors[0];
        var ship = Ship(m, s, 1, 1, new Vector3(200, 0, 0), new Vector3(time == 0 ? 200 : -200, 0, 0));
        ship.InitClock(stateTime);
        s.ResolveExplosion(new Explosion(3, 20, 100, time, Vector3.Zero, null), 1);
        Assert.Equal(.6f, ship.Fraction, 5);
        var damage = Assert.Single(site.ShipDamage);
        Assert.Equal(time, damage.Time);
        Assert.Equal(Vector3.Zero, damage.Target);
        Assert.Equal(Vector3.Zero, damage.Blast);
    }

    [Fact]
    public void Splash_passes_NoWarn_and_NoDebris_without_losing_the_damage_row()
    {
        var (m, site) = Domain();
        var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, Vector3.Zero);
        Target("station", m, s, owner, Vector3.Zero);
        var launcher = Ship(m, s, 2, 2, new Vector3(1000, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal((byte)0x63, Assert.Single(site.StationDamageTypes));
        Assert.Equal((byte)0x63, Assert.Single(site.ShipDamage).Type);
    }

    [Theory]
    [InlineData("station", false, 1)]
    [InlineData("station", true, .6)]
    [InlineData("missile", false, 1)]
    [InlineData("missile", true, .6)]
    [InlineData("armedProbe", false, .6)] // P100/P491 contract: armed probes also take friendly hits
    [InlineData("armedProbe", true, .6)]
    [InlineData("probe", false, .6)]
    [InlineData("rescueProbe", false, .6)]
    [InlineData("asteroid", false, .6)]
    public void Target_specific_friendly_fire_rules_preserve_probe_contract(string kind, bool allow, double expected)
    {
        var (m, _) = Domain(); m.AllowFriendlyFire = allow;
        var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, new Vector3(1000, 0, 0));
        var fraction = Target(kind, m, s, owner, Vector3.Zero);
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, owner), 0);
        Assert.Equal((float)expected, fraction(), 5);
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("station")]
    [InlineData("missile")]
    public void Non_ship_launchers_obey_friendly_immunity(string kind)
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, kind == "ship" ? Vector3.Zero : new Vector3(1000, 0, 0));
        var fraction = Target(kind, m, s, owner, Vector3.Zero);
        var launcher = new Probe(2, s, DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]), new Vector3(1000, 0, 0), 0, 1, owner);
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(1f, fraction());
        m.AllowFriendlyFire = true;
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(.6f, fraction(), 5);
    }

    [Theory]
    [InlineData("ship")]
    [InlineData("station")]
    [InlineData("missile")]
    [InlineData("armedProbe")]
    public void Allied_sides_obey_target_immunity_and_can_repair(string kind)
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        m.AlliedSidePairs.Add((2, 1)); // reverse ordering is still allied
        var owner = Ship(m, s, 1, 1, kind == "ship" ? Vector3.Zero : new Vector3(1000, 0, 0));
        var fraction = Target(kind, m, s, owner, Vector3.Zero, armor: kind == "station");
        var launcher = Ship(m, s, 2, 2, new Vector3(1000, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(kind == "armedProbe" ? .6f : 1f, fraction(), 5);
        s.ResolveExplosion(new Explosion(3, kind == "station" ? 70 : 20, 100, 0, Vector3.Zero, null), 0);
        float damaged = fraction(); Assert.True(damaged < 1);
        s.ResolveExplosion(new Explosion(3, -10, 100, 0, Vector3.Zero, launcher), 0);
        Assert.Equal(damaged + .2f, fraction(), 5);
    }

    [Theory]
    [InlineData("asteroid")]
    [InlineData("station")]
    [InlineData("missile")]
    [InlineData("armedProbe")]
    public void Every_repairable_target_heals_caps_and_uses_launcher_experience(string kind)
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, new Vector3(1000, 0, 0));
        var fraction = Target(kind, m, s, owner, Vector3.Zero, armor: kind == "station");
        s.ResolveExplosion(new Explosion(3, kind == "station" ? 70 : 20, 100, 0, Vector3.Zero, null), 0);
        float damaged = fraction();
        for (int i = 0; i < 4; i++) owner.AddKillBonus(); // multiplier=1.25
        s.ResolveExplosion(new Explosion(3, -10, 100, 0, Vector3.Zero, owner), 0);
        Assert.Equal(damaged + .25f, fraction(), 5);
        s.ResolveExplosion(new Explosion(3, -1000, 100, 0, Vector3.Zero, owner), 0);
        Assert.Equal(1f, fraction());
    }

    [Theory]
    [InlineData(105, 1)] // exactly at surface radius: untouched
    [InlineData(104, .99996)] // just inside: small nonzero damage
    [InlineData(5, .6)] // explosion touches surface: full amount
    [InlineData(0, .6)] // overlapping: no damage amplification
    public void Surface_boundary_is_strict_and_overlap_is_full_damage(float distance, double expected)
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        var ship = Ship(m, s, 1, 1, new Vector3(distance, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, null), 0);
        Assert.Equal((float)expected, ship.Fraction, 5);
    }

    [Fact]
    public void Moving_missile_is_sampled_at_blast_time()
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        var missile = new Missile(1, s, MissileType, new Vector3(200, 0, 0), new Vector3(200, 0, 0), new Vector3(1, 0, 0), 1);
        s.Add(missile);
        s.ResolveExplosion(new Explosion(3, 20, 100, 0, Vector3.Zero, null), 1);
        Assert.Equal(.6f, missile.Fraction, 5);
    }

    [Fact]
    public void Lethal_splash_removes_targets_and_notifies_each_death_once_even_without_a_launcher()
    {
        var (m, site) = Domain(); var s = m.Sectors[0];
        var owner = Ship(m, s, 1, 1, new Vector3(1000, 0, 0));
        foreach (string kind in new[] { "asteroid", "station", "missile", "probe" }) Target(kind, m, s, owner, Vector3.Zero);
        var explosion = new Explosion(3, 1000, 100, 0, Vector3.Zero, null);
        s.ResolveExplosion(explosion, 0); s.ResolveExplosion(explosion, 0);
        Assert.Empty(s.Mineable); Assert.Empty(s.StationRuntimes);
        Assert.Single(s.Models); Assert.Contains(owner, s.Models);
        Assert.Equal(1, site.AsteroidKills); Assert.Equal(1, site.StationKills);
        Assert.Equal(1, site.MissileKills); Assert.Equal(1, site.ProbeKills);
    }

    [Fact]
    public void Missile_destroyed_by_splash_creates_its_secondary_blast_once()
    {
        var (m, site) = Domain(); var s = m.Sectors[0];
        var missile = new Missile(1, s, MissileType with { HitPoints = 10, BlastPower = 20, BlastRadius = 50 },
            new Vector3(200, 0, 0), Vector3.Zero, new Vector3(1, 0, 0), 0);
        s.Add(missile);
        var ship = Ship(m, s, 2, 1, new Vector3(220, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 10, 0, missile.Position, null), 0);
        Assert.DoesNotContain(missile, s.Models);
        Assert.Equal(1, site.MissileKills);
        Assert.Equal(.804f, ship.Fraction, 5); // only secondary: .7²*20*2/100
    }

    [Fact]
    public void Missile_killed_at_the_zero_position_sentinel_does_not_create_a_secondary_blast()
    {
        // Native FedSrv.CPP:12521-12526 reserves zero for resonator impacts.
        var (m, site) = Domain(); var s = m.Sectors[0];
        var missile = new Missile(1, s, MissileType with { HitPoints = 10 },
            Vector3.Zero, Vector3.Zero, new Vector3(1, 0, 0), 0);
        s.Add(missile);
        var ship = Ship(m, s, 2, 1, new Vector3(55, 0, 0));
        s.ResolveExplosion(new Explosion(3, 20, 10, 0, Vector3.Zero, null), 0);
        Assert.DoesNotContain(missile, s.Models);
        Assert.Equal(1, site.MissileKills);
        Assert.Equal(1f, ship.Fraction); // outside the original blast, inside a secondary's radius
    }

    [Fact]
    public void Expiring_missile_blast_does_not_destroy_or_notify_its_source_twice()
    {
        var (m, site) = Domain(); var s = m.Sectors[0];
        var missile = new Missile(1, s, MissileType with { Lifespan = .5f, HitPoints = 1, BlastPower = 20 },
            new Vector3(200, 0, 0), Vector3.Zero, new Vector3(1, 0, 0), 0);
        s.Add(missile);
        var ship = Ship(m, s, 2, 1, new Vector3(220, 0, 0));
        s.Update(1);
        Assert.DoesNotContain(missile, s.Models);
        Assert.Equal(.711f, ship.Fraction, 5);
        Assert.Equal(1, site.MissileKills);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_mission_tick_routes_a_warp_blast_regardless_of_sector_update_order(bool reverseOrder)
    {
        var (m, site) = Domain(5, pairedWarps: true, reverseOrder: reverseOrder);
        var source = m.Sectors.Single(s => s.Id == 0); var destination = m.Sectors.Single(s => s.Id == 1);
        var victim = Ship(m, destination, 1, 1, new Vector3(-970, 0, 0));
        var launcher = Ship(m, source, 2, 1, Vector3.Zero);
        source.Add(new Missile(3, source, MissileType with { EabmCapabilities = 4 }, new Vector3(1000, 0, 0),
            Vector3.Zero, new Vector3(0, 0, 1), 0, launcher));
        m.Update(.05);
        Assert.Equal(1, site.WarpBombs);
        m.Update(5.049);
        Assert.Equal(1f, victim.Fraction);
        m.Update(5.051);
        Assert.Equal(.775f, victim.Fraction, 5);
        m.Update(5.1);
        Assert.Equal(.775f, victim.Fraction, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dangling_warp_has_no_fabricated_destination_and_only_dual_can_hit_source(bool dual)
    {
        var (m, _) = Domain(); var s = m.Sectors[0];
        var raw = DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
        { WarpId = 10, DestinationId = -1, Radius = 5, Position = [1000, 0, 0] };
        ((List<ResolvedWarp>)typeof(Sector).GetField("_warps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!).Add(new ResolvedWarp(raw, null));
        var victim = Ship(m, s, 1, 1, new Vector3(1030, 0, 0));
        s.Add(new Missile(2, s, MissileType with { EabmCapabilities = dual ? (short)2 : (short)4 },
            new Vector3(1000, 0, 0), Vector3.Zero, new Vector3(0, 0, 1), 0));
        Pass(s, "ResolveWarpBombTriggers", 1);
        Pass(s, "ResolveWarpBombDetonations", 6);
        Assert.Equal(dual ? .775f : 1f, victim.Fraction, 5);
    }

    [Fact]
    public void Due_bombs_detonate_in_deadline_order_with_each_delay_captured_at_trigger()
    {
        var (m, _) = Domain(10, pairedWarps: true);
        var source = m.Sectors[0]; var destination = m.Sectors[1];
        var victim = Ship(m, destination, 1, 1, new Vector3(-970, 0, 0));
        var pos = new Vector3(1000, 0, 0);
        source.Add(new Missile(2, source, MissileType with { EabmCapabilities = 4 }, pos,
            Vector3.Zero, new Vector3(0, 0, 1), 0));
        Pass(source, "ResolveWarpBombTriggers", 1); // due at 11
        m.Core.Constants.FloatConstants[32] = 5;
        source.Add(new Missile(3, source, MissileType with { EabmCapabilities = 4, Power = 10 }, pos,
            Vector3.Zero, new Vector3(0, 0, 1), 0));
        Pass(source, "ResolveWarpBombTriggers", 2); // due at 7, ahead of older bomb
        Pass(source, "ResolveWarpBombDetonations", 6.999);
        Assert.Equal(1f, victim.Fraction);
        Pass(source, "ResolveWarpBombDetonations", 7);
        Assert.Equal(.8875f, victim.Fraction, 5);
        Pass(source, "ResolveWarpBombDetonations", 10.999);
        Assert.Equal(.8875f, victim.Fraction, 5);
        Pass(source, "ResolveWarpBombDetonations", 11);
        Assert.Equal(.6625f, victim.Fraction, 5);
    }

    private static void Pass(Sector s, string method, double now) =>
        typeof(Sector).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, [now]);

    private static ResolvedWarp Warp(Sector source, Sector destination, short id, short partnerId, Vector3 pos)
    {
        var warp = new ResolvedWarp(DataWarpIGC.Decode(new byte[DataWarpIGC.Size]) with
        { WarpId = id, DestinationId = partnerId, ClusterId = source.Id, Radius = 5, Position = [pos.X, pos.Y, pos.Z] }, destination);
        ((List<ResolvedWarp>)typeof(Sector).GetField("_warps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!).Add(warp);
        return warp;
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(4, 7)]
    [InlineData(4, 10)]
    [InlineData(2, 5)]
    [InlineData(6, 7)]
    public void Warp_bomb_uses_core_delay_destination_and_optional_dual_source(short capabilities, float delay)
    {
        var (m, site) = Domain(delay);
        var a = m.Sectors[0]; var b = m.Sectors[1];
        var sourcePos = new Vector3(1000, 0, 0); var destPos = new Vector3(-1000, 0, 0);
        Warp(a, b, 10, 11, sourcePos); Warp(b, a, 11, 10, destPos);
        var sourceVictim = Ship(m, a, 1, 1, sourcePos + new Vector3(30, 0, 0));
        var destVictim = Ship(m, b, 2, 1, destPos + new Vector3(30, 0, 0));
        var launcher = Ship(m, a, 3, 1, Vector3.Zero);
        var missile = new Missile(4, a, MissileType with { EabmCapabilities = capabilities }, sourcePos,
            Vector3.Zero, new Vector3(0, 0, 1), 0, launcher);
        a.Add(missile);
        Pass(a, "ResolveWarpBombTriggers", 1);
        Assert.DoesNotContain(missile, a.Models);
        Assert.Equal(1, site.WarpBombs);
        Assert.Equal(1, site.MissileKills);
        Pass(a, "ResolveWarpBombDetonations", 1 + delay - .001);
        Assert.Equal(1f, sourceVictim.Fraction); Assert.Equal(1f, destVictim.Fraction);
        Pass(a, "ResolveWarpBombDetonations", 1 + delay);
        Assert.Equal(.775f, destVictim.Fraction, 5); // Power, null launcher, destination coordinates and matrix
        Assert.Equal((capabilities & 2) != 0 ? .775f : 1f, sourceVictim.Fraction, 5);
        Pass(a, "ResolveWarpBombDetonations", 2 + delay);
        Assert.Equal(.775f, destVictim.Fraction, 5); // one detonation
    }
}
