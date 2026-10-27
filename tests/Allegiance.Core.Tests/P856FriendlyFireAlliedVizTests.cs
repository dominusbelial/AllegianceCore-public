using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P856 — <c>bAllowFriendlyFire</c> and <c>bAllowAlliedViz</c> are real game
/// options instead of test-only setters.
/// <para>Three layers are proven here, per option:
/// <b>(1) config</b> — per-game JSON, the environment override and the CLI switch
/// all reach <see cref="GameConfig"/> with native <c>MissionParams::Reset</c>
/// defaults (both off: <c>Igc/igc.h:1243,1246</c>);
/// <b>(2) mission param / wire</b> — the flags land on
/// <see cref="Mission.AllowFriendlyFire"/>/<see cref="Mission.AllowAlliedViz"/>
/// and in the native <c>MISSIONDEF(38)</c> bitfield block at the struct's real
/// bit positions (<c>bAllowAlliedViz</c> bit 6 = params byte 108,
/// <c>bAllowFriendlyFire</c> bit 28 = byte 111; <c>Igc/igc.h:1127,1149</c>);
/// <b>(3) behaviour</b> — every native consumer on and off: ship, station,
/// missile, armed/sensor/RescueAny probe damage (<c>Igc/shipIGC.cpp:1222-1228</c>,
/// <c>Igc/stationIGC.cpp:126-133</c>, <c>Igc/missileigc.h:86-94</c>,
/// <c>Igc/probeigc.h:74-89</c>), allied damage through the live alliance groups,
/// the visibility sweep's allied legs (<c>FedSrv/FedSrv.CPP:10893-10895</c>,
/// <c>:10989-11000</c>), the start-of-game allied cluster reveal
/// (<c>Igc/mapmakerigc.cpp:473-474,1020-1058</c>) and the probe firing reveal
/// (<c>Igc/probeigc.cpp:448-463</c>).</para>
/// The mission-level tests use the real PCore001 core and XXV_simple map through
/// <see cref="Corpus"/> (no sockets, no server), the same data-derived pattern as
/// the other Core sim tests.
/// </summary>
public sealed class P856FriendlyFireAlliedVizTests
{
    // ── config: JSON / environment / CLI ───────────────────────────────────

    [Fact]
    public void Json_environment_and_cli_carry_both_options_with_native_defaults()
    {
        var config = JsonSerializer.Deserialize("""
            {"games":[{"name":"A","allowFriendlyFire":true,"allowAlliedViz":true},
                      {"name":"B"}]}
            """, ServerJsonContext.Default.ServerConfig)!;
        Assert.True(config.Games[0].AllowFriendlyFire);
        Assert.True(config.Games[0].AllowAlliedViz);
        // Native MissionParams::Reset defaults: friendly fire stays false (the
        // bitfield block is ZeroMemory'd and never assigned, igc.h:1246) and
        // bAllowAlliedViz is set false explicitly (igc.h:1243).
        Assert.False(config.Games[1].AllowFriendlyFire);
        Assert.False(config.Games[1].AllowAlliedViz);

        var roundtrip = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!;
        Assert.True(roundtrip.Games[0].AllowFriendlyFire);
        Assert.True(roundtrip.Games[0].AllowAlliedViz);

        // Empty environment values preserve the per-game JSON.
        ServerConfigLoader.ApplyMissionOptionEnvironment(config, "", null);
        Assert.False(config.Games[1].AllowFriendlyFire);

        // Environment applies to every configured game (container-wide override).
        ServerConfigLoader.ApplyMissionOptionEnvironment(config, "1", "true");
        Assert.All(config.Games, g => Assert.True(g.AllowFriendlyFire && g.AllowAlliedViz));

        // CLI overrides the environment, which overrides JSON.
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("friendly-fire", "0")]));
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("allied-viz", "false")]));
        Assert.All(config.Games, g => Assert.False(g.AllowFriendlyFire || g.AllowAlliedViz));
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("friendlyfire", "true")]));
        Assert.All(config.Games, g => Assert.True(g.AllowFriendlyFire));
        // Per-game values stay independent.
        Assert.NotSame(config.Games[0], config.Games[1]);

        // A malformed switch fails loudly instead of silently reading as off.
        Assert.Contains("allied-viz", Assert.Throws<InvalidDataException>(() =>
            ServerConfigLoader.ApplyCli(config, [("allied-viz", "maybe")])).Message);
        // Both switches need a configured game (same rule as --goal/--goals).
        Assert.Contains("friendly-fire", Assert.Throws<InvalidDataException>(() =>
            ServerConfigLoader.ApplyCli(new ServerConfig(), [("friendly-fire", "1")])).Message);
    }

    // ── wire: native MISSIONDEF bitfield block ────────────────────────────

    [Fact]
    public void Missiondef_carries_both_option_bits_at_the_native_offsets()
    {
        // Absolute message offsets: header 4 + MissionParams field offsets.
        // Native: bAllowAlliedViz bit 6 of the bool:1 block that starts at
        // MissionParams+108 (Igc/igc.h:1127) and bAllowFriendlyFire bit 28
        // (Igc/igc.h:1149) → params bytes 108 and 111, absolute 112 and 115.
        // MSVC packs bool:1 units LSB-first; the pre-existing bLockGameOpen bit
        // 18 (params byte 110, absolute 114) is the independent anchor.
        const int ParamsBitfield = 4 + 108;
        var off = new MissionDef().Encode();
        Assert.Equal(864, off.Length);
        for (int i = 0; i < 5; i++) Assert.Equal(0, off[ParamsBitfield + i]);

        var on = new MissionDef { AllowFriendlyFire = true, AllowAlliedViz = true }.Encode();
        Assert.Equal(0x40, on[ParamsBitfield] & 0x40);          // bAllowAlliedViz, bit 6
        Assert.Equal(0x10, on[ParamsBitfield + 3] & 0x10);      // bAllowFriendlyFire, bit 28
        Assert.Equal(0, on[ParamsBitfield + 1] & 0x40);         // no bit bleed into other bytes
        Assert.Equal(0, on[ParamsBitfield + 4] & 0x0f);         // bits 32..35 stay clear

        var decoded = MissionDef.Decode(on);
        Assert.True(decoded.AllowFriendlyFire);
        Assert.True(decoded.AllowAlliedViz);
        Assert.False(MissionDef.Decode(off).AllowFriendlyFire);
        Assert.False(MissionDef.Decode(off).AllowAlliedViz);

        // A peer's bytes decode independently of our encoder (a set alias
        // bit for bLockGameOpen alongside the two options).
        var peer = new MissionDef { BLockGameOpen = true }.Encode();
        peer[ParamsBitfield] |= 0x40;
        peer[ParamsBitfield + 3] |= 0x10;
        var fromPeer = MissionDef.Decode(peer);
        Assert.True(fromPeer.AllowFriendlyFire && fromPeer.AllowAlliedViz && fromPeer.BLockGameOpen);

        // The client-side snapshot keeps both option flags.
        var world = new Allegiance.ClientCore.State.ClientWorld();
        world.Apply(new MissionDef { AllowFriendlyFire = true, AllowAlliedViz = true });
        Assert.True(world.MissionAllowFriendlyFire);
        Assert.True(world.MissionAllowAlliedViz);
    }

    // ── behaviour: friendly fire ──────────────────────────────────────────

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, "ship", false)] [InlineData(true, "ship", true)]
    [InlineData(false, "station", false)] [InlineData(true, "station", true)]
    [InlineData(false, "missile", false)] [InlineData(true, "missile", true)]
    public void Friendly_fire_option_gates_ship_station_and_missile_damage(bool allow, string kind, bool damaged)
    {
        Require.Artwork();
        var (mission, sector, attacker) = Domain(allow);
        var launcher = attacker;
        var position = new Vector3(100, 0, 0);
        switch (kind)
        {
            case "ship":
            {
                // Igc/shipIGC.cpp:1222-1228 (same side, amount >= 0).
                var target = NewShip(mission, sector, 9100, 0, position);
                target.ReceiveDamage(DamageType(mission.Core, target.HullType.DefenseType), 20, 0,
                    position, position, launcher);
                Assert.Equal(damaged, target.Fraction < 1f);
                break;
            }
            case "station":
            {
                // Igc/stationIGC.cpp:126-133 (AlliedSides variant of the gate).
                var station = NewStation(mission, sector, 9200, 0, position);
                float before = station.ShieldFraction + station.ArmorFraction;
                station.ReceiveDamage(DamageType(mission.Core, station.Type.DefenseTypeArmor), 20, 0,
                    position, position, launcher);
                Assert.Equal(damaged, station.ShieldFraction + station.ArmorFraction < before);
                break;
            }
            case "missile":
            {
                // Igc/missileigc.h:86-94.
                var missile = new Missile(9300, sector, mission.Core.MissileTypes.Values.First(),
                    position, Vector3.Zero, new Vector3(0, 0, 1), 0, launcher);
                sector.Add(missile);
                missile.ReceiveDamage(DamageType(mission.Core, mission.Core.MissileTypes.Values.First().DefenseType),
                    20, 0, position, position, launcher);
                Assert.Equal(damaged, missile.Fraction < 1f);
                break;
            }
        }
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    // Igc/probeigc.h:74-89: FF off + friendly launcher blocks an ARMED probe
    // only; sensor and RescueAny probes stay damageable (mmf/Xynth #98).
    [InlineData(false, "armed", false)] [InlineData(true, "armed", true)]
    [InlineData(false, "sensor", true)] [InlineData(true, "sensor", true)]
    [InlineData(false, "rescueAny", true)] [InlineData(true, "rescueAny", true)]
    public void Friendly_probe_damage_follows_native_armed_probe_immunity(bool allow, string kind, bool damaged)
    {
        Require.Artwork();
        var (mission, sector, attacker) = Domain(allow);
        var armed = mission.Core.ProbeTypes.Values.First(t => t.ProjectileTypeId > 0 && t.DtRipcord < 0);
        var type = kind switch
        {
            "armed" => armed with { EabmCapabilities = 0 },
            "rescueAny" => armed with { EabmCapabilities = DataProbeTypeIGC.EabmRescueAny },
            _ => armed with { ProjectileTypeId = -1, EabmCapabilities = 0 },
        };
        // The RescueAny variant is deliberately ARMED: native exempts it from
        // the gate before the armed-probe check (probeigc.h:74-89).
        Assert.Equal(kind is "armed" or "rescueAny", type.ProjectileTypeId > 0);
        var probe = new Probe(9400, sector, type, new Vector3(100, 0, 0), 0, 0, attacker);
        sector.Add(probe);
        probe.ReceiveDamage(DamageType(mission.Core, type.DefenseType), 20, 0,
            probe.Position, probe.Position, attacker);
        Assert.Equal(damaged, probe.Fraction < 1f);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)] [InlineData(true, true)]
    public void Allied_damage_obeys_the_option_through_live_alliance_groups(bool allow, bool damaged)
    {
        Require.Artwork();
        var (mission, sector, attacker) = Domain(allow);
        // The server's UpdateAlliances (FedSrv/fsmission.cpp:5638-5811, port
        // P190) writes exactly this field; production never touches
        // AlliedSidePairs, so the allied leg must read the live group.
        Ally(mission, 0, 1);
        Assert.True(mission.AreSidesFriendly(0, 1));
        Assert.False(mission.AreSidesFriendly(0, 2));
        var alliedTarget = NewShip(mission, sector, 9500, 1, new Vector3(100, 0, 0));
        alliedTarget.ReceiveDamage(DamageType(mission.Core, alliedTarget.HullType.DefenseType), 20, 0,
            alliedTarget.Position, alliedTarget.Position, attacker);
        Assert.Equal(damaged, alliedTarget.Fraction < 1f);
        // Same-side damage follows the same option.
        var friendlyTarget = NewShip(mission, sector, 9501, 0, new Vector3(200, 0, 0));
        friendlyTarget.ReceiveDamage(DamageType(mission.Core, friendlyTarget.HullType.DefenseType), 20, 0,
            friendlyTarget.Position, friendlyTarget.Position, attacker);
        Assert.Equal(damaged, friendlyTarget.Fraction < 1f);
        // Self-splash (launcher == victim, same side) follows the same option
        // (shipIGC.cpp:1223-1224; P649 already proved the toggle for splash).
        var self = attacker;
        self.ReceiveDamage(DamageType(mission.Core, self.HullType.DefenseType), 20, 0,
            self.Position, self.Position, self);
        Assert.Equal(damaged, self.Fraction < 1f);
    }

    // ── behaviour: allied visualization ──────────────────────────────────

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)] [InlineData(true, true)]
    public void Allied_sides_see_each_others_models_only_with_allied_visualization(bool allow, bool visible)
    {
        Require.Artwork();
        var mission = NewMission(2, alliedViz: allow);
        Ally(mission, 0, 1);
        var sector = CleanSector(mission);
        var alliedShip = NewShip(mission, sector, 9600, 1, Vector3.Zero);
        // No side-0 scanner and no side-0 model exists in this sector.
        sector.ResolveCrossSideVisibility();
        Assert.Equal(visible, alliedShip.SeenBySide(0));
        Assert.True(alliedShip.SeenBySide(1));       // own side, always (FedSrv.CPP:10907)
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)] [InlineData(true, true)]
    public void Allied_scans_are_shared_with_allies_only_with_allied_visualization(bool allow, bool visible)
    {
        Require.Artwork();
        var mission = NewMission(3, alliedViz: allow);
        Ally(mission, 0, 1);                          // 0 and 1 allied, 2 is not
        var sector = CleanSector(mission);
        var allyScout = NewShip(mission, sector, 9700, 1, new Vector3(100, 0, 0));
        var enemy = NewShip(mission, sector, 9701, 2, Vector3.Zero);
        sector.ResolveCrossSideVisibility();
        // The ally's own scanner really does see the enemy (otherwise the test's
        // premise — "the contact is spotted by an ally" — would be false).
        Assert.True(enemy.SeenBySide(1));
        // FedSrv.CPP:10989-11000: with bAllowAlliedViz the ally of the spotting
        // side learns the contact too (same last spotter).
        Assert.Equal(visible, enemy.SeenBySide(0));
        Assert.False(enemy.SeenBySide(0) && enemy.GetCurrentEye(0) && !allow);
        _ = allyScout;
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)] [InlineData(true, true)]
    public void Start_reveals_an_allied_home_cluster_only_with_allied_visualization(bool allow, bool visible)
    {
        Require.Artwork();
        var mission = NewMission(2, alliedViz: allow);
        Ally(mission, 0, 1);
        // A station-free sector adopted as side 1's home: side 0 owns nothing in
        // it and has no scanner there, so only the allied reveal can inform it.
        var home = CleanSector(mission);
        NewStation(mission, home, 9900, 1, Vector3.Zero);
        var rock = new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        {
            AsteroidId = 9901, ClusterId = home.Id, Position = [2000, 0, 0],
            HitPoints = 100, Radius = 5,
        });
        mission.PlaceAsteroid(rock, home);
        Assert.NotNull(rock);
        Assert.False(rock.SeenBySide(0));             // nothing has run a sweep yet
        mission.SeedStartingVisibility();
        // Igc/mapmakerigc.cpp:1020-1058: RevealAlliedClusters shows every model
        // of the ally's home cluster — statics latch, so the rock stays known.
        Assert.Equal(visible, rock.SeenBySide(0));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)] [InlineData(true, true)]
    public void Probe_firing_reveal_reaches_allied_sides_through_live_alliance_groups(bool allow, bool visible)
    {
        Require.Artwork();
        var mission = NewMission(3, alliedViz: allow);
        Ally(mission, 0, 2);                          // 0 and 2 allied, 1 is the enemy
        var sector = CleanSector(mission);
        var owner = NewShip(mission, sector, 9800, 0, new Vector3(-100, 0, 0));
        var projectile = mission.Core.ProjectileTypes.Values.First() with
        { Speed = 1000, Lifespan = 10, BlastRadius = 0 };
        ((IDictionary<short, DataProjectileTypeIGC>)mission.Core.ProjectileTypes)[projectile.ProjectileTypeId] = projectile;
        var type = mission.Core.ProbeTypes.Values.First() with
        {
            DtRipcord = -1, Lifespan = 100, Radius = 2, Signature = 1,
            ProjectileTypeId = projectile.ProjectileTypeId, EabmCapabilities = 0x48, Ammo = 0,
            DtimeBurst = 1, Accuracy = 1, ScannerRange = 10000,
        };
        var probe = new Probe(9801, sector, type, Vector3.Zero, 0, 0, owner);
        sector.Add(probe);
        var target = NewShip(mission, sector, 9802, 1, new Vector3(100, 0, 0));
        Assert.False(target.SeenBySide(0));
        probe.Update(5.1);
        // Igc/probeigc.cpp:448-463: the first shot makes the probe visible to
        // every side with a scanner, and (bAllowAlliedViz) to allied sides
        // without one. Side 2 has no ship/probe/station in this sector.
        Assert.Equal(visible, probe.SeenBySide(2));
        Assert.False(probe.SeenBySide(2) && !allow);
        Assert.False(probe.SeenBySide(2) && probe.GetCurrentEye(2) && !allow);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static readonly Lazy<StaticCore> LazyCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!Path.GetFileName(path).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase)) continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("PCore001.igc not found in the corpus");
    });

    private static readonly Lazy<MissionMap> LazyMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!Path.GetFileName(path).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase)) continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("XXV_simple.igc not found in the corpus");
    });

    private static Mission NewMission(int sideCount, bool allowFriendlyFire = false, bool alliedViz = false)
    {
        var core = LazyCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var sides = Enumerable.Range(0, sideCount)
            .Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray();
        var mission = Mission.Assemble(core, LazyMap.Value, sides, new NullIgcSite());
        mission.Stage = MissionStage.Started;
        foreach (var side in mission.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        mission.AllowFriendlyFire = allowFriendlyFire;
        mission.AllowAlliedViz = alliedViz;
        return mission;
    }

    /// <summary>Two-side started mission with an attacker on side 0 and an empty
    /// sector for the targets.</summary>
    private static (Mission Mission, Sector Sector, Ship Attacker) Domain(bool allowFriendlyFire = false)
    {
        var mission = NewMission(3, allowFriendlyFire);
        var sector = CleanSector(mission);
        var attacker = NewShip(mission, sector, 9000, 0, new Vector3(-100, 0, 0));
        return (mission, sector, attacker);
    }

    private static DataHullTypeIGC Hull(StaticCore core) => core.HullTypes.Values
        .First(h => h.HitPoints > 0f && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    private static Ship NewShip(Mission mission, Sector sector, short id, short side, Vector3 position)
    {
        var ship = new Ship(id, Hull(mission.Core), position, Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, sector);
        return ship;
    }

    private static Station NewStation(Mission mission, Sector sector, short id, short side, Vector3 position)
    {
        var type = mission.Core.StationTypes.Values
            .Where(t => t.MaxArmorHitPoints > 0 && t.MaxShieldHitPoints > 0
                && !t.HasCapability(DataStationTypeIGC.SabmPedestal))
            .OrderBy(t => t.StationTypeId).First();
        var data = new DataStationIGC([position.X, position.Y, position.Z], [0, 1, 0], [1, 0, 0],
            [0, 0, 0, 1], sector.Id, side, (short)(20000 + id), type.StationTypeId, 1, 1, "P856 base");
        var station = new Station(data, type);
        mission.PlaceStation(station, sector);
        return station;
    }

    /// <summary>A mission sector with no stations, so no ambient station scanner
    /// can establish visibility for the tests (stations are removed when the map
    /// has none such, mirroring P832a's goal test setup).</summary>
    private static Sector CleanSector(Mission mission)
    {
        var sector = mission.Sectors.FirstOrDefault(s => s.StationRuntimes.Count == 0)
            ?? mission.Sectors.First();
        foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
        return sector;
    }

    /// <summary>The alliance the server's <c>UpdateAlliances</c> writes: both
    /// sides carry the same non-NA group (<c>Igc/igc.h:4297-4305</c>).</summary>
    private static void Ally(Mission mission, short a, short b)
    {
        const sbyte group = 1;
        mission.Sides.First(s => s.Id == a).AllyGroup = group;
        mission.Sides.First(s => s.Id == b).AllyGroup = group;
    }

    /// <summary>A damage row with a real positive factor against
    /// <paramref name="defense"/>, so "no damage" can only come from the
    /// friendly-fire gate (not from a zeroed damage-matrix cell).</summary>
    private static byte DamageType(StaticCore core, byte defense)
    {
        for (byte type = 0; type < core.Constants.DamageConstants.Length; type++)
            if (defense < core.Constants.DamageConstants[type].Length
                && core.Constants.DamageConstants[type][defense] > 0.5f)
                return type;
        throw new InvalidOperationException($"no damaging row for defense {defense}");
    }
}
