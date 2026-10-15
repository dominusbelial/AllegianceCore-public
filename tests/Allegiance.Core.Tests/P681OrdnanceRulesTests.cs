using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

// Native source root: ~/Development/Allegiance/src/. Synthetic loaded core,
// real model updates and host callbacks; no wall clock or sockets required.
public sealed class P681OrdnanceRulesTests
{
    private sealed class Site : IIgcSite
    {
        public readonly List<IProbeIgc> Activated = [];
        public readonly List<Projectile> Rounds = [];
        public int MissileKills;
        public void ActivateTeleportProbe(IProbeIgc probe) => Activated.Add(probe);
        public void ProjectileCreatedEvent(Projectile projectile) => Rounds.Add(projectile);
        public void MissileDestroyedEvent(Missile missile) => MissileKills++;
    }

    private static (Mission Mission, Sector Sector, Site Site) Domain(float blastRadius = 0)
    {
        var bytes = new byte[DataConstantsIGC.Size + DataProjectileTypeIGC.Size];
        for (int i = DataConstantsIGC.DamageOffset; i < DataConstantsIGC.DamageOffset + 1600; i += 4)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i), 1);
        int offset = DataConstantsIGC.Size;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 60), blastRadius);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 64), 100);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 68), 10);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 72), 1);
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize, Version = 1, Body = bytes,
            BodyLength = bytes.Length, BytesConsumed = bytes.Length,
            Records = [new(IgcObjectType.Constants, DataConstantsIGC.Size, 0),
                new(IgcObjectType.ProjectileType, DataProjectileTypeIGC.Size, offset)]
        });
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var site = new Site();
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []), [], site);
        mission.Stage = MissionStage.Started;
        return (mission, mission.Sectors.Single(), site);
    }

    private static Ship Ship(Mission m, Sector s, short id, short side, Vector3 position)
    {
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        { HitPoints = 100, Length = 2, Mass = 1, Signature = 1, Speed = 100 };
        var ship = new Ship(id, hull, position, Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(ship, s);
        return ship;
    }
    private static DataProbeTypeIGC ProbeType => DataProbeTypeIGC.Decode(new byte[DataProbeTypeIGC.Size]) with
    {
        Lifespan = 100, HitPoints = 100, Radius = 2, Signature = 1, ProjectileTypeId = 1,
        EabmCapabilities = 0x48, DtimeBurst = .5f, Accuracy = 1, DtRipcord = -1, ScannerRange = 1000
    };
    private static DataMissileTypeIGC MissileType => DataMissileTypeIGC.Decode(new byte[DataMissileTypeIGC.Size]) with
    { Lifespan = 100, HitPoints = 100, Signature = 1, ReadyTime = 1, Acceleration = 10, TurnRate = 100 };

    // Igc/missileigc.cpp:363-405: activation inside a tick only accelerates for
    // the remaining time. Native subtracts backward*acceleration*activeDt
    // for the inactive prefix, hence ADDS forward displacement (:396-404).
    [Fact]
    public void Missile_activation_inside_tick_uses_only_active_interval()
    {
        var (_, s, _) = Domain();
        var missile = new Missile(1, s, MissileType, Vector3.Zero, new(0, 0, 2), new(0, 0, 1), 0);
        s.Add(missile);
        missile.Update(.5); Assert.Equal(1f, missile.Position.Z);
        missile.Update(1.5);
        Assert.Equal(7f, missile.Velocity.Z);
        Assert.Equal(10.5f, missile.Position.Z);
    }

    // Igc/missileigc.cpp:260-352: independent equation residual verifies both
    // approaching roots and the far intercept, plus the zero-relative-speed case.
    [Theory]
    [InlineData(100, 0, 10)]
    [InlineData(100, -60, 10)]
    [InlineData(100, 60, 10)]
    [InlineData(100, -10, 10)]
    public void Acceleration_intercept_satisfies_native_quartic(float distance, float relativeSpeed, float acceleration)
    {
        var t = MissileIntercept.Solve(new(distance, 0, 0), new(relativeSpeed, 0, 0), acceleration, 1);
        Assert.True(float.IsFinite(t) && t > 0);
        Assert.InRange(MathF.Abs(MathF.Abs(distance + relativeSpeed * t) - .5f * acceleration * t * t), 0, .01f);
        if (relativeSpeed < -50) Assert.True(t < 2, "approaching target must select the early root");
    }

    // Igc/missileigc.cpp:371-389: acceleration, rather than current missile
    // speed, determines the lead. A zero initial speed is a useful discriminator.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missile_leads_moving_target_and_drops_it_after_sector_departure(bool staleSnapshot)
    {
        var (m, s, _) = Domain();
        var target = Ship(m, s, 2, 1, new(100, 0, 0)); target.Velocity = new(0, 10, 0);
        var missile = new Missile(1, s, MissileType with { ReadyTime = 0 }, Vector3.Zero,
            Vector3.Zero, new(1, 0, 0), 0, target: target);
        s.Add(missile); missile.Update(.1);
        Assert.True(missile.Forward.Y > .3f);
        if (staleSnapshot) target.Sector = new Sector(DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 1 });
        else s.Remove(target);
        missile.Update(.2); Assert.Null(missile.Target);
    }

    // Igc/missileigc.cpp:354-360: the crossing tick still moves; the NEXT
    // update detonates at the prior position and emits destruction once.
    [Fact]
    public void Missile_burnout_waits_for_previous_clock_and_explodes_at_previous_position()
    {
        var (m, s, site) = Domain();
        var target = Ship(m, s, 2, 1, new(12, 0, 0));
        var missile = new Missile(1, s, MissileType with
        { Lifespan = 1, ReadyTime = 100, BlastPower = 20, BlastRadius = 1 },
            new(2, 0, 0), new(10, 0, 0), new(1, 0, 0), 0);
        s.Add(missile); missile.Update(1);
        Assert.Contains(missile, s.Models); Assert.Equal(1f, target.Fraction); Assert.Equal(0, site.MissileKills);
        missile.Update(2);
        Assert.DoesNotContain(missile, s.Models); Assert.Equal(.8f, target.Fraction, 4);
        Assert.Equal(new Vector3(12, 0, 0), missile.Position); Assert.Equal(1, site.MissileKills);
        missile.Update(3); Assert.Equal(1, site.MissileKills);
    }

    // Igc/projectileIGC.cpp:120-143: exact expiry-position blast on the crossing
    // tick; keep the round until the following tick without a repeated blast.
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    public void Projectile_crossing_blast_is_at_expiry_then_terminates_next_update(double crossing)
    {
        var (m, s, _) = Domain();
        var target = Ship(m, s, 2, 1, new(12, 0, 0));
        var type = m.Core.ProjectileTypes[1] with { Lifespan = 1, BlastPower = 20, BlastRadius = 1 };
        var round = new Projectile(1, s, type, new(2, 0, 0), new(10, 0, 0), new(1, 0, 0), 0);
        s.Add(round); round.Update(crossing);
        Assert.Equal(.8f, target.Fraction, 4); Assert.Contains(round, s.Models);
        round.Update(crossing + .1);
        Assert.DoesNotContain(round, s.Models); Assert.Equal(.8f, target.Fraction, 4);
        round.Update(crossing + .2); Assert.Equal(.8f, target.Fraction, 4);
    }

    // Igc/probeigc.cpp:151-154,333-349: zero-delay initialization; strict
    // previous-clock edge, inclusive now, and expiry wins over activation.
    [Theory]
    [InlineData(-1, 100, 0)]
    [InlineData(0, 100, 1)]
    [InlineData(2, 100, 1)]
    [InlineData(2, 2, 0)]
    public void Teleport_activation_is_once_and_expiry_takes_precedence(float delay, float lifespan, int expected)
    {
        var (_, s, site) = Domain();
        var probe = new Probe(1, s, ProbeType with { DtRipcord = delay, Lifespan = lifespan, ProjectileTypeId = -1 }, Vector3.Zero, 0, 0);
        s.Add(probe); probe.Update(1.99);
        Assert.Equal(delay == 0 ? 1 : 0, site.Activated.Count);
        probe.Update(2); probe.Update(2); probe.Update(3);
        Assert.Equal(expected, site.Activated.Count);
        if (expected > 0) Assert.Same(probe, site.Activated.Single());
    }

    // Igc/probeigc.cpp:390-435: category priority changes only for AOE rounds.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1.01f, true)]
    public void Probe_AOE_prioritizes_missiles_otherwise_ships(float blastRadius, bool expectsMissile)
    {
        var (m, s, site) = Domain(blastRadius);
        var ship = Ship(m, s, 2, 1, new(0, 50, 0));
        var missile = new Missile(3, s, MissileType, new(100, 0, 0), Vector3.Zero, new(1, 0, 0), 0, ship);
        s.Add(missile);
        var probe = new Probe(1, s, ProbeType with { EabmCapabilities = 0xc8 }, Vector3.Zero, 0, 0);
        s.Add(probe); probe.Update(5.1);
        var round = Assert.Single(site.Rounds);
        Assert.True(expectsMissile ? round.Forward.X > .99f : round.Forward.Y > .99f);
    }

    // Igc/probeigc.cpp:268-299,390-399; probeigc.h:163-191.
    [Theory]
    [InlineData("no_target")]
    [InlineData("absent")]
    [InlineData("friendly")]
    [InlineData("dead")]
    [InlineData("lifepod")]
    [InlineData("allied")]
    [InlineData("out_of_scan")]
    public void Probe_specific_target_must_be_present_hostile_damageable_and_scanned(string invalid)
    {
        var (m, s, site) = Domain();
        var target = Ship(m, s, 2, 1, new(100, 0, 0));
        Ship(m, s, 3, 1, new(0, 50, 0)); // valid alternate must never be chosen
        if (invalid == "absent") s.Remove(target);
        if (invalid == "friendly") target.SideId = 0;
        if (invalid == "allied") m.AlliedSidePairs.Add((0, 1));
        if (invalid == "lifepod")
        {
            s.Remove(target);
            target = new Ship(2, target.HullType with { HabmCapabilities = DataHullTypeIGC.HabmLifepod },
                target.Position, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            m.PlaceShip(target, s);
        }
        if (invalid == "dead") target.ReceiveDamage(0, 1000, 0, target.Position, target.Position, null);
        var probe = new Probe(1, s, ProbeType with { EabmCapabilities = 0x1048,
            ScannerRange = invalid == "out_of_scan" ? 0 : 1000 }, Vector3.Zero, 0, 0)
            { Target = invalid == "no_target" ? null : target };
        s.Add(probe); probe.Update(5.1); Assert.Empty(site.Rounds);
    }

    // Igc/probeigc.cpp:390-435: known static stations outrank nearer ships
    // and missiles, including ShootOnlyTarget's model adapter path.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Probe_prioritizes_known_station_and_excludes_allied_station(bool specific, bool allied)
    {
        var (m, s, site) = Domain(10);
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 4, ClusterId = 0, SideId = 1, Position = [100, 0, 0] },
            DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
            { Radius = 2, MaxArmorHitPoints = 100, Signature = 1 });
        m.PlaceStation(station, s); station.SideVisibility.SetSideVisibility(0, true);
        if (allied) m.AlliedSidePairs.Add((0, 1));
        Ship(m, s, 2, 2, new(0, 50, 0));
        var probe = new Probe(1, s, ProbeType with
        { EabmCapabilities = (short)(specific ? 0x10e8 : 0xe8), ScannerRange = 60 }, Vector3.Zero, 0, 0)
        { Target = specific ? EquipmentTarget.Resolve(s, IgcObjectType.Station, 4) : null };
        s.Add(probe); probe.Update(5.1);
        var round = Assert.Single(site.Rounds);
        Assert.True(allied ? round.Forward.Y > .99f : round.Forward.X > .99f);
    }

    // Igc/probeigc.h:175-191: asteroid line of sight blocks a dynamic target;
    // carrying a flag bypasses the scanner test as in native.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_requires_line_of_sight_except_for_flag_carrier(bool flag)
    {
        var (m, s, site) = Domain();
        var target = Ship(m, s, 2, 1, new(100, 0, 0));
        if (flag) target.Flag = 0;
        m.PlaceAsteroid(new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        { AsteroidId = 4, ClusterId = 0, Position = [50, 0, 0], Radius = 10, HitPoints = 100 }), s);
        var probe = new Probe(1, s, ProbeType, Vector3.Zero, 0, 0); s.Add(probe);
        probe.Update(5.1); Assert.Equal(flag ? 1 : 0, site.Rounds.Count);
    }

    // Igc/probetypeigc.cpp:43-47; probeigc.cpp:471,496-505. Exercise the real
    // CVH resolver, frame transform/scale, rotation, and historical catch-up.
    [Fact]
    public void Probe_burst_uses_historical_clocks_rotated_muzzle_and_launcher_fallback()
    {
        var (m, s, site) = Domain();
        Ship(m, s, 2, 1, new(100, 0, 0));
        s.CollisionHulls = new CollisionHullCache(_ => new CvhFile
        {
            Radius = 4, EllipseEquation = new(4, 4, 4), EllipseRadiusMultiplier = 4, Hulls = [],
            Frames = [new("wepemt", new(0, 0, -4), new(0, 0, -1))]
        });
        var probe = new Probe(1, s, ProbeType, Vector3.Zero, 0, 0); s.Add(probe);
        probe.Update(5); Assert.Empty(site.Rounds); probe.Update(6.1);
        Assert.Equal(new[] { 5d, 5.5, 6 }, site.Rounds.Select(r => r.LastUpdate));
        Assert.All(site.Rounds, r =>
        {
            Assert.Equal(10, r.TimeExpire - r.LastUpdate); Assert.Same(probe, r.Launcher);
            Assert.Same(probe, r.OriginProbe); Assert.Equal(2f, r.Position.X, 4);
        });
        Assert.Equal(1, s.CollisionHulls.Count);
    }
    // Igc/probeigc.cpp:373-379,525-526: idle time cannot accumulate a burst
    // before the previous model update; firing resumes at that prior clock.
    [Fact]
    public void Probe_idle_clock_clamps_next_burst_to_previous_update()
    {
        var (m, s, site) = Domain();
        var probe = new Probe(1, s, ProbeType, Vector3.Zero, 0, 0); s.Add(probe);
        probe.Update(40); Assert.Empty(site.Rounds);
        Ship(m, s, 2, 1, new(100, 0, 0)); probe.Update(40.1);
        Assert.Equal(40, Assert.Single(site.Rounds).LastUpdate);
    }

    // Igc/igc.h:4297-4305 and probeigc.cpp:268: production alliances use
    // live AllyGroup values, not only explicit side pairs in isolated hosts.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Probe_respects_live_alliance_groups_and_reacquires_when_alliance_ends()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var civ = core.Civilizations.Keys.First();
        var site = new Site();
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var m = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []),
            [new SideSpec(0, "A", civ), new SideSpec(1, "B", civ)], site);
        foreach (var side in m.Sides) { side.GlobalAttributes = GlobalAttributeSet.Identity; side.AllyGroup = 0; }
        var sector = m.Sectors[0]; Ship(m, sector, 2, 1, new(0, 0, 50));
        var probe = new Probe(1, sector, core.ProbeTypes[30], Vector3.Zero, 0, 0); sector.Add(probe);
        probe.Update(5.1); Assert.Empty(site.Rounds);
        m.Sides[1].AllyGroup = null;
        probe.Update(5.2); Assert.Single(site.Rounds);
    }

    // Igc/probeigc.cpp:468-471 and zlib/orientation.cpp:220-230: retaining
    // up/roll changes a transverse muzzle offset; setting forward alone loses it.
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(-90)]
    public void Probe_transverse_muzzle_preserves_current_up_when_turning(float roll)
    {
        var (m, s, site) = Domain(); Ship(m, s, 2, 1, new(100, 0, 0));
        var probe = new Probe(1, s, ProbeType, Vector3.Zero, 0, 0);
        probe.Orientation.Set(new(0, 0, -1), new(0, 1, 0));
        probe.Orientation.Roll(roll * MathF.PI / 180);
        probe.Hull = new CvhFile
        {
            Radius = 4, EllipseEquation = new(4, 4, 4), EllipseRadiusMultiplier = 4, Hulls = [],
            Frames = [new("wepemt", new(4, 0, 0), new(0, 0, -1))]
        };
        s.Add(probe); probe.Update(5.1);
        var round = Assert.Single(site.Rounds);
        // Roll 0 keeps up=Y, right=Z. Roll +90 makes up antiparallel
        // to the target (use oldForward=-Z); -90 makes it parallel (use +Z).
        var expected = roll == 0 ? new Vector3(0, 0, -2) : new Vector3(0, roll > 0 ? -2 : 2, 0);
        Assert.True((round.Position - expected).Length() < .0001f, $"{round.Position} vs {expected}");
        Assert.True((probe.Orientation.GetForward() - new Vector3(1, 0, 0)).Length() < .0001f);
    }

}
