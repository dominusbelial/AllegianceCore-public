namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Xunit;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

/// <summary>
/// P750 — native asteroid types. The generated asteroids (procedural map and
/// research-showcase rocks) must carry the REAL native model/icon/hit-points/
/// radius/name-prefix from the transcribed table (asteroidigc.cpp:126-137),
/// never the invented "rock_mine"/"rock_build" names. These tests fail on the
/// pre-P750 code (which emitted "rock_mine"/"rock_build" and blank icons) and
/// pass after.
/// </summary>
public sealed class P750NativeAsteroidTypesTests
{
    // Expected values are transcribed from asteroidTypes[] (asteroidigc.cpp:126-137):
    //   index: capabilities / hp / radius / model / icon / prefix
    //   0,1 : 0          / 25000 / 400 / bgrnd03|bgrnd05 / meteoricon / ""
    //   2,3 : buildable  / 10000 / 200 / bgrnd03|bgrnd05 / meteoricon / ""
    //   4,5 : mineHe3    / 25000 / 100 / bgrnd55|bgrnd56 / heliumrock / "He"
    //   6   : special<<0 / 25000 / 200 / bgrnd51          / hotrock    / "U"
    //   7   : special<<1 / 25000 / 200 / bgrnd52          / copperrock / "Si"
    //   8   : special<<2 / 25000 / 200 / bgrnd53          / carbonrock / "C"
    private static readonly string[] ValidModels =
        ["bgrnd03", "bgrnd05", "bgrnd51", "bgrnd52", "bgrnd53", "bgrnd55", "bgrnd56"];
    private static readonly string[] ValidIcons =
        ["meteoricon", "heliumrock", "hotrock", "copperrock", "carbonrock"];

    private static readonly Lazy<StaticCore> GenCore = new(MapGenTestCore.LoadRepresentative);

    private static MissionMap Generate(MapGenType type, int teams, int mapSize = 0)
        => MissionMapGenerator.Generate(new MapGenParams
        {
            Type = type,
            Teams = teams,
            CivIds = GenCore.Value.Civilizations.Values
                .Where(c => c.InitialStationTypeId != -1)
                .OrderBy(c => c.CivilizationId)
                .Take(teams)
                .Select(c => (int)c.CivilizationId)
                .ToArray(),
            MapSize = mapSize,
        }, GenCore.Value, 0.0);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Table_matches_native_models_icons_hp_radius_prefix()
    {
        Require.Artwork();
        var rows = Enumerable.Range(0, NativeAsteroidTypes.Count)
            .Select(i => (i, NativeAsteroidTypes.GetTypeDefaults(i)))
            .ToArray();

        // Row 0/1 — huge neutral (capabilities 0).
        foreach (var (i, r) in rows.Where(t => t.Item2.Capabilities == 0))
        {
            Assert.True(new[] { "bgrnd03", "bgrnd05" }.Contains(r.ModelName));
            Assert.Equal("meteoricon", r.IconName);
            Assert.Equal(25000f, r.HitPoints);
            Assert.Equal(400, r.Radius);
            Assert.Equal(0f, r.Ore);
        }
        // Row 2/3 — buildable.
        foreach (var (_, r) in rows.Where(t => t.Item2.Capabilities == DataAsteroidIGC.AabmBuildable))
        {
            Assert.True(new[] { "bgrnd03", "bgrnd05" }.Contains(r.ModelName));
            Assert.Equal("meteoricon", r.IconName);
            Assert.Equal(10000f, r.HitPoints);
            Assert.Equal(200, r.Radius);
        }
        // Row 4/5 — helium (prefix "He", ore 1.0).
        foreach (var (_, r) in rows.Where(t => t.Item2.Capabilities == DataAsteroidIGC.AabmMineHe3))
        {
            Assert.True(new[] { "bgrnd55", "bgrnd56" }.Contains(r.ModelName));
            Assert.Equal("heliumrock", r.IconName);
            Assert.Equal(25000f, r.HitPoints);
            Assert.Equal(100, r.Radius);
            Assert.Equal(1.0f, r.Ore);
            Assert.Equal("He", r.Prefix);
        }
        // Special rows 6/7/8 — uranium/silicon/carbon (prefix U/Si/C).
        var special = rows.Where(t => t.i >= 6).ToArray();
        Assert.Equal(3, special.Length);
        var byModel = special.ToDictionary(t => t.Item2.ModelName, t => t.Item2);
        Assert.Equal("U", byModel["bgrnd51"].Prefix);      // uranium → hotrock
        Assert.Equal("Si", byModel["bgrnd52"].Prefix);     // silicon → copperrock
        Assert.Equal("C", byModel["bgrnd53"].Prefix);      // carbon → carbonrock
        Assert.Equal("hotrock", byModel["bgrnd51"].IconName);
        Assert.Equal("copperrock", byModel["bgrnd52"].IconName);
        Assert.Equal("carbonrock", byModel["bgrnd53"].IconName);
        foreach (var (_, r) in special)
        {
            Assert.Equal(25000f, r.HitPoints);
            Assert.Equal(200, r.Radius);
        }

        // Every row's model/icon is a real native name (none invented).
        foreach (var (_, r) in rows)
        {
            Assert.Contains(r.ModelName, ValidModels);
            Assert.Contains(r.IconName, ValidIcons);
            Assert.DoesNotContain("rock_", r.ModelName);
            Assert.DoesNotContain("rock_", r.IconName);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GetRandomType_picks_within_the_right_pair_per_capability()
    {
        Require.Artwork();
        var hits = new HashSet<int>();
        for (int i = 0; i < 200; i++)
        {
            hits.Add(NativeAsteroidTypes.GetRandomType(0));                       // huge pair 0..1
            hits.Add(NativeAsteroidTypes.GetRandomType(DataAsteroidIGC.AabmBuildable)); // generic 2..3
            hits.Add(NativeAsteroidTypes.GetRandomType(DataAsteroidIGC.AabmMineHe3));    // minable 4..5
        }
        Assert.Equal([0, 1], hits.Where(x => x < 2).Distinct().Order().ToArray());
        Assert.Equal([2, 3], hits.Where(x => x is 2 or 3).Distinct().Order().ToArray());
        Assert.Equal([4, 5], hits.Where(x => x is 4 or 5).Distinct().Order().ToArray());
        Assert.DoesNotContain(hits, x => x >= 6);   // GetRandomType never emits specials
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ResolveTypeIndex_maps_random_kinds_and_exact_special_rows()
    {
        Require.Artwork();
        for (int i = 0; i < 100; i++)
        {
            Assert.InRange(NativeAsteroidTypes.ResolveTypeIndex(0), 0, 1);
            Assert.InRange(NativeAsteroidTypes.ResolveTypeIndex(DataAsteroidIGC.AabmBuildable), 2, 3);
            Assert.InRange(NativeAsteroidTypes.ResolveTypeIndex(DataAsteroidIGC.AabmMineHe3), 4, 5);
        }
        Assert.Equal(6, NativeAsteroidTypes.ResolveTypeIndex((short)(DataAsteroidIGC.AabmSpecial << 0)));
        Assert.Equal(7, NativeAsteroidTypes.ResolveTypeIndex((short)(DataAsteroidIGC.AabmSpecial << 1)));
        Assert.Equal(8, NativeAsteroidTypes.ResolveTypeIndex((short)(DataAsteroidIGC.AabmSpecial << 2)));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AsteroidName_uses_native_prefix_and_number_formula()
    {
        Require.Artwork();
        // Native GetAsteroidName (common.cpp:2786-2801): prefix + (1024 + ((id+1093)*5237) % 8713).
        // Expected numbers computed independently from that formula.
        Assert.Equal("He3828", NativeAsteroidTypes.AsteroidName(4, 5273)); // He prefix, id 5273 → 3828
        Assert.Equal("U9160", NativeAsteroidTypes.AsteroidName(6, 549));   // U prefix, id 549 → 9160
        // The "\0a" (huge/buildable) prefix truncates to empty natively; the port
        // keeps a bare, non-blank number so generated rocks stay identifiable.
        Assert.Equal("8726", NativeAsteroidTypes.AsteroidName(0, 123));    // id 123 → 8726, no prefix
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Generated_ring_map_asteroids_carry_native_models_icons_hp()
    {
        Require.Artwork();
        var map = Generate(MapGenType.SingleRing, 2);
        Assert.NotEmpty(map.Asteroids);
        foreach (var a in map.Asteroids)
        {
            Assert.Contains(a.ModelName, ValidModels);
            Assert.Contains(a.IconName, ValidIcons);
            Assert.False(a.ModelName.StartsWith("rock_"), $"invented model {a.ModelName}");
            Assert.False(a.IconName.StartsWith("rock_"), $"invented icon {a.IconName}");
        }

        // Per-type fidelity: each rock's model comes from its capability's native
        // row (GetRandomType picks one of the pair, common.cpp:3036/3075 +
        // asteroidigc.cpp:231-255); hp/radius are constant across the pair and
        // match the table.
        foreach (var a in map.Asteroids)
        {
            if ((a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0)
            {
                Assert.True(new[] { "bgrnd55", "bgrnd56" }.Contains(a.ModelName));
                Assert.Equal("heliumrock", a.IconName);
                Assert.Equal(25000f, a.HitPoints);
                Assert.InRange(a.Radius, (short)100, (short)200);   // randomInt(100,200) incl.
                Assert.StartsWith("He", a.Name);
            }
            else if ((a.AabmCapabilities & DataAsteroidIGC.AabmSpecial) != 0)
            {
                Assert.Equal("bgrnd51", a.ModelName);
                Assert.Equal("hotrock", a.IconName);
                Assert.Equal(25000f, a.HitPoints);
                Assert.InRange(a.Radius, (short)200, (short)400);
            }
            else if ((a.AabmCapabilities & DataAsteroidIGC.AabmBuildable) != 0)
            {
                Assert.True(new[] { "bgrnd03", "bgrnd05" }.Contains(a.ModelName));
                Assert.Equal("meteoricon", a.IconName);
                Assert.Equal(10000f, a.HitPoints);
                Assert.InRange(a.Radius, (short)200, (short)400);
            }
            else // capabilities 0 — huge neutral
            {
                Assert.True(new[] { "bgrnd03", "bgrnd05" }.Contains(a.ModelName));
                Assert.Equal("meteoricon", a.IconName);
                Assert.Equal(25000f, a.HitPoints);
                Assert.InRange(a.Radius, (short)400, (short)800);
            }
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Generated_brawl_asteroids_use_native_huge_and_special_rows()
    {
        Require.Artwork();
        var map = Generate(MapGenType.Brawl, 2);

        // Scatter rocks (capabilities 0) are huge: bgrnd03/05, hp 25000, radius 400..800.
        foreach (var a in map.Asteroids.Where(a => a.AabmCapabilities == 0))
        {
            Assert.True(new[] { "bgrnd03", "bgrnd05" }.Contains(a.ModelName));
            Assert.Equal("meteoricon", a.IconName);
            Assert.Equal(25000f, a.HitPoints);
            Assert.InRange(a.Radius, 400, 800);
        }
        // Special rocks (0x10 in this port's single-subtype assignment) are uranium bgrnd51.
        foreach (var a in map.Asteroids.Where(a => (a.AabmCapabilities & DataAsteroidIGC.AabmSpecial) != 0))
        {
            Assert.Equal("bgrnd51", a.ModelName);
            Assert.Equal("hotrock", a.IconName);
            Assert.Equal(25000f, a.HitPoints);
            Assert.InRange(a.Radius, 200, 400);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Research_showcase_rocks_carry_native_models_and_no_rock_names()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new Cfg
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            Port = 0,
            Showcase = new() { TechMode = "research" },
            Games = [new() { Name = "P750 research", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        var rocks = server.Missions[0].Sectors.SelectMany(s => s.Asteroids).ToList();
        Assert.NotEmpty(rocks);
        foreach (var a in rocks)
        {
            // Core P750 guarantee: never an invented "rock_" name; every rock
            // (the map's own AND the showcase-added research rocks) carries a
            // real native model/icon.
            Assert.Contains(a.ModelName, ValidModels);
            Assert.Contains(a.IconName, ValidIcons);
            Assert.False(a.ModelName.StartsWith("rock_"), $"invented model {a.ModelName}");
            Assert.False(a.IconName.StartsWith("rock_"), $"invented icon {a.IconName}");

            // When this rock's capability is one of the 9 native table rows, its
            // model/icon must come from that row. (A real corpus map can carry
            // exotic caps like gold/lots-He3 with no native row, and real maps
            // legitimately override per-rock hit points, so only model/icon
            // correspondence is asserted here — hp/radius-from-table is covered
            // strictly by the port-generated map tests above.)
            var rows = Enumerable.Range(0, NativeAsteroidTypes.Count)
                .Select(NativeAsteroidTypes.GetTypeDefaults)
                .Where(r => r.Capabilities == a.AabmCapabilities)
                .ToList();
            if (rows.Count == 0) continue;
            Assert.Contains(rows, r => r.ModelName == a.ModelName);
            Assert.Contains(rows, r => r.IconName == a.IconName);
        }
    }
}
