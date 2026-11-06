namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit;

/// <summary>
/// P939 — the special (branch-resource) asteroids of a generated map.
///
/// Native has one rule for every map, generated or file: <c>PopulateCluster</c>
/// creates each special with <c>IasteroidIGC::GetSpecialAsterioid(pmp,
/// offset++)</c> where <c>offset</c> starts at 1, or at
/// <c>randomInt(1, NumberSpecialAsteroids(pmp))</c> when that count is not 1
/// (<c>Igc/common.cpp:3052-3058</c>). Both helpers key on the mission's three
/// research-path switches — <c>bAllowExpansionPath</c> /
/// <c>bAllowTacticalPath</c> / <c>bAllowSupremacyPath</c>
/// (<c>Igc/asteroidigc.cpp:179-229</c>), so the special kinds are Uranium,
/// Silicon and Carbonaceous (<c>asteroidigc.cpp:134-136</c>,
/// <c>nFirstSpecialType = 6</c> at <c>:146</c>).
///
/// The native kind table is transcribed below as the test oracle (independent of
/// the port's implementation) so these tests assert against the C++ rule.
/// </summary>
public sealed class P939SpecialAsteroidTests
{
    private static readonly Lazy<StaticCore> GenCore = new(MapGenTestCore.LoadRepresentative);

    private static int[] CivIds(int n) =>
        GenCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId)
            .Where(c => c.InitialStationTypeId != -1
                && GenCore.Value.StationTypes.ContainsKey(c.InitialStationTypeId))
            .Take(n)
            .Select(c => (int)c.CivilizationId)
            .ToArray();

    private const short U = (short)(DataAsteroidIGC.AabmSpecial << 0);   // asteroidigc.cpp:134
    private const short Si = (short)(DataAsteroidIGC.AabmSpecial << 1);  // asteroidigc.cpp:135
    private const short C = (short)(DataAsteroidIGC.AabmSpecial << 2);   // asteroidigc.cpp:136

    /// <summary>Native <c>NumberSpecialAsteroids</c> count table
    /// (<c>Igc/asteroidigc.cpp:181</c>) indexed by
    /// <c>supremacy·4 + tactical·2 + expansion·1</c>.</summary>
    private static readonly int[] NativeCount = [3, 1, 1, 2, 1, 2, 2, 3];

    private static int Code(bool expansion, bool tactical, bool supremacy)
        => (supremacy ? 4 : 0) + (tactical ? 2 : 0) + (expansion ? 1 : 0);

    /// <summary>Native <c>GetSpecialAsterioid</c>'s kind for a type index
    /// (<c>Igc/asteroidigc.cpp:190-229</c>): 0 = U, 1 = Si, 2 = C.</summary>
    private static int NativeKind(int code, int index) => code switch
    {
        0 or 7 => index % 3,
        1 => 0,
        2 => 1,
        3 => index % 2,
        4 => 2,
        5 => ((index % 2) == 0) ? 0 : 2,
        6 => 1 + (index % 2),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private static short NativeCapability(int code, int index)
        => (short)(DataAsteroidIGC.AabmSpecial << NativeKind(code, index));

    /// <summary>The three special classes are distinct capability bits —
    /// Uranium 0x10, Silicon 0x20, Carbonaceous 0x40
    /// (<c>asteroidigc.cpp:134-136</c>) — so "is a special rock" is membership of
    /// that set, not a test against 0x10 alone.</summary>
    private const short SpecialClasses = (short)(U | Si | C);

    private static bool IsSpecial(DataAsteroidIGC a)
        => (a.AabmCapabilities & SpecialClasses) != 0;

    /// <summary>Every legal native special sequence for a sector: the type index
    /// starts at the cluster's draw <c>offset</c> — 1 when the type count is 1,
    /// else <c>randomInt(1, count)</c> inclusive (<c>common.cpp:3052-3054</c>).</summary>
    private static string[] LegalSequences(int code, int length)
    {
        int count = NativeCount[code];
        var offsets = count == 1 ? new[] { 1 } : Enumerable.Range(1, count).ToArray();
        return offsets.Select(offset => string.Join(",",
            Enumerable.Range(0, length).Select(i => KindName(NativeKind(code, offset + i))))).ToArray();
    }

    private static string KindName(int kind) => kind switch { 0 => "U", 1 => "Si", 2 => "C", _ => "?" };

    private static string Describe(DataAsteroidIGC a) =>
        $"0x{a.AabmCapabilities:x2}";

    private static MissionMap Generate(int seed, MapGenType type)
        => MissionMapGenerator.Generate(new MapGenParams
        {
            Type = type,
            Teams = 2,
            CivIds = CivIds(2),
            MapSize = 0,
            Seed = seed,
        }, GenCore.Value, 0.0);

    /// <summary>Per-sector special capability sequences in map order — the
    /// map's whole special-asteroid census, for failure messages.</summary>
    private static string SpecialCensus(MissionMap map)
        => string.Join(" | ", map.Clusters.OrderBy(c => c.ClusterId).Select(c =>
            $"sector {c.ClusterId}: "
            + string.Join(",", map.Asteroids.Where(a => a.ClusterId == c.ClusterId && IsSpecial(a))
                .Select(Describe))));

    private static string[] ObservedKinds(MissionMap map, short clusterId)
        => map.Asteroids.Where(a => a.ClusterId == clusterId && IsSpecial(a))
            .Select(a => a.AabmCapabilities switch { U => "U", Si => "Si", C => "C", _ => "?" })
            .ToArray();

    /// <summary>⭐ Native has one special-asteroid rule for every map: with all
    /// three research paths enabled (the <c>MissionParams</c> default,
    /// <c>Igc/igc.h:1247-1249</c>) <c>NumberSpecialAsteroids</c> is 3 and the
    /// kinds cycle Uranium → Silicon → Carbonaceous from the drawn offset
    /// (<c>asteroidigc.cpp:181-187,198-201</c>). A generated SingleRing map must
    /// therefore carry all three classes, not only the plain 0x10 Uranium rock
    /// P937 recorded.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Generated_singlering_specials_cover_all_three_native_classes()
    {
        Require.Artwork();
        var map = Generate(seed: 1123059318, MapGenType.SingleRing);

        var kinds = map.Asteroids.Where(IsSpecial).Select(a => Describe(a)).ToList();
        Assert.NotEmpty(kinds);
        foreach (var expected in new[] { U, Si, C })
            Assert.True(map.Asteroids.Any(a => a.AabmCapabilities == expected),
                $"generated SingleRing seed=1123059318 has no 0x{expected:x2} special rock; census: {SpecialCensus(map)}");
        Assert.All(map.Asteroids.Where(IsSpecial), a => Assert.Contains(a.AabmCapabilities, new[] { U, Si, C }));
    }

    /// <summary>⭐ The same native rule on a PinWheel map (whose centrality
    /// multiplier doubles the centre's special count, mapmakerigc.cpp:1901-1917).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Generated_pinwheel_specials_cover_all_three_native_classes()
    {
        Require.Artwork();
        var map = Generate(seed: 939, MapGenType.PinWheel);

        Assert.Contains(map.Asteroids, IsSpecial);
        foreach (var expected in new[] { U, Si, C })
            Assert.True(map.Asteroids.Any(a => a.AabmCapabilities == expected),
                $"generated PinWheel seed=939 has no 0x{expected:x2} special rock; census: {SpecialCensus(map)}");
    }

    /// <summary>⭐ Each sector's special sequence is the native cycle starting at
    /// the cluster's own draw offset (1..3 inclusive), never a fixed class and
    /// never a pattern the C++ cannot produce.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Generated_special_sequence_per_sector_follows_the_native_all_paths_cycle()
    {
        Require.Artwork();
        foreach (var type in new[] { MapGenType.SingleRing, MapGenType.PinWheel })
        {
            var map = Generate(seed: 4242, type);
            foreach (var cluster in map.Clusters)
            {
                var observed = ObservedKinds(map, cluster.ClusterId);
                if (observed.Length == 0) continue;
                var legal = LegalSequences(Code(expansion: true, tactical: true, supremacy: true), observed.Length);
                Assert.True(legal.Contains(string.Join(",", observed)),
                    $"{type} sector {cluster.ClusterId}: special sequence {string.Join(",", observed)} "
                    + $"is not a native rotation ({string.Join(" / ", legal)})");
            }
        }
    }

    /// <summary>⭐ RNG: a fixed seed reproduces the identical generated map
    /// (P811's contract) — the special-kind rule must not make the generator
    /// non-deterministic.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_seed_reproduces_the_same_map_with_the_native_special_rule()
    {
        Require.Artwork();
        foreach (var type in new[] { MapGenType.SingleRing, MapGenType.PinWheel })
        {
            var a = Generate(seed: 773311, type);
            var b = Generate(seed: 773311, type);
            Assert.Equal(Fingerprint(a), Fingerprint(b));
        }
    }

    private static string Fingerprint(MissionMap map)
        => string.Join("\n", map.Asteroids.Select(a =>
            $"{a.AsteroidId}:{a.ClusterId}:0x{a.AabmCapabilities:x2}:{a.Radius}:"
            + $"{a.Position[0]:R}:{a.Position[1]:R}:{a.Position[2]:R}:{a.Ore:R}:{a.ModelName}"));

    private static string FingerprintHash(MissionMap map)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Fingerprint(map))));

    // ── The native table, all 8 path combinations ───────────────────────────

    /// <summary>⭐ Native <c>NumberSpecialAsteroids</c> + <c>GetSpecialAsterioid</c>
    /// for every one of the 8 research-path combinations, type indices 0..5
    /// (<c>Igc/asteroidigc.cpp:179-229</c>). The expected sequences are the C++
    /// table transcribed independently: U = 0x10 Uranium, Si = 0x20 Silicon,
    /// C = 0x40 Carbonaceous (<c>asteroidigc.cpp:134-136</c>). The third value is
    /// <c>NumberSpecialAsteroids</c> — the inclusive upper bound of the cluster's
    /// starting offset (<c>common.cpp:3052-3054</c>).</summary>
    [Theory]
    [InlineData(false, false, false, "U,Si,C,U,Si,C", 3)]   // c=0
    [InlineData(true, false, false, "U,U,U,U,U,U", 1)]      // c=1 expansion only
    [InlineData(false, true, false, "Si,Si,Si,Si,Si,Si", 1)] // c=2 tactical only
    [InlineData(true, true, false, "U,Si,U,Si,U,Si", 2)]    // c=3 expansion+tactical
    [InlineData(false, false, true, "C,C,C,C,C,C", 1)]      // c=4 supremacy only
    [InlineData(true, false, true, "U,C,U,C,U,C", 2)]       // c=5 expansion+supremacy
    [InlineData(false, true, true, "Si,C,Si,C,Si,C", 2)]    // c=6 tactical+supremacy
    [InlineData(true, true, true, "U,Si,C,U,Si,C", 3)]      // c=7 all paths
    public void Special_table_matches_native_for_every_path_combination(
        bool expansion, bool tactical, bool supremacy, string kinds, int count)
    {
        Assert.Equal(count, MissionMapGenerator.NumberSpecialAsteroids(expansion, tactical, supremacy));

        var expected = kinds.Split(',').Select(k => k switch { "U" => U, "Si" => Si, "C" => C, _ => (short)-1 }).ToArray();
        // Indices 0..5: native's running type index (offset++) is evaluated on this
        // index, so every position of the cycle is pinned.
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], MissionMapGenerator.SpecialAsteroidCapability(expansion, tactical, supremacy, index));

        // The cluster's start index is 1, or one draw in [1, count] inclusive.
        var rng = new CountingRandom();
        int offset = MissionMapGenerator.SpecialAsteroidOffset(rng, expansion, tactical, supremacy);
        if (count == 1)
        {
            Assert.Equal(1, offset);
            Assert.Equal(0, rng.Draws);   // native draws nothing when the count is 1
        }
        else
        {
            Assert.Equal((1, count + 1), Assert.Single(rng.Calls));
            Assert.InRange(offset, 1, count);
        }
    }

    /// <summary>The type index is never reset per rock: two rocks of a multi-class
    /// sector advance the native cycle (<c>GetSpecialAsterioid(pmp, offset++)</c>,
    /// <c>common.cpp:3057</c>).</summary>
    [Fact]
    public void Special_class_advances_with_the_running_offset()
    {
        Assert.Equal(Si, MissionMapGenerator.SpecialAsteroidCapability(true, true, true, 1));
        Assert.Equal(C, MissionMapGenerator.SpecialAsteroidCapability(true, true, true, 2));
        Assert.Equal(U, MissionMapGenerator.SpecialAsteroidCapability(true, true, true, 3));
        Assert.Equal(Si, MissionMapGenerator.SpecialAsteroidCapability(true, true, false, 3));
        Assert.Equal(Si, MissionMapGenerator.SpecialAsteroidCapability(false, true, true, 2));
    }

    /// <summary>A counting RNG that records every draw: proves the offset rule's
    /// draw count and range without touching the generator's seeded stream.</summary>
    private sealed class CountingRandom : IMapRandom
    {
        public int Draws;
        public List<(int Min, int Max)> Calls { get; } = [];

        public int Next() { Draws++; return 0; }
        public int Next(int min, int max) { Draws++; Calls.Add((min, max)); return min; }
        public double NextDouble() { Draws++; return 0.5; }
    }

    // ── Generated maps ──────────────────────────────────────────────────────

    private static MissionMap GenerateWithPaths(int seed, MapGenType type,
        bool expansion, bool tactical, bool supremacy)
        => MissionMapGenerator.Generate(new MapGenParams
        {
            Type = type,
            Teams = 2,
            CivIds = CivIds(2),
            MapSize = 0,
            Seed = seed,
            AllowExpansionPath = expansion,
            AllowTacticalPath = tactical,
            AllowSupremacyPath = supremacy,
        }, GenCore.Value, 0.0);

    /// <summary>⭐ One enabled path means <c>NumberSpecialAsteroids</c> is 1, so
    /// every special rock of a generated map is that path's own class
    /// (<c>asteroidigc.cpp:181-187,203-217</c>): expansion → Uranium 0x10,
    /// tactical → Silicon 0x20, supremacy → Carbonaceous 0x40.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(true, false, false, (short)0x10)]
    [InlineData(false, true, false, (short)0x20)]
    [InlineData(false, false, true, (short)0x40)]
    public void Generated_single_path_map_places_only_that_paths_class(
        bool expansion, bool tactical, bool supremacy, short expected)
    {
        Require.Artwork();
        foreach (var type in new[] { MapGenType.SingleRing, MapGenType.PinWheel })
        {
            var map = GenerateWithPaths(seed: 4242, type, expansion, tactical, supremacy);
            var specials = map.Asteroids.Where(IsSpecial).ToArray();
            Assert.Contains(specials, a => a.AabmCapabilities == expected);
            Assert.All(specials, a => Assert.Equal(expected, a.AabmCapabilities));
            // Model/icon follow the class's native row (asteroidigc.cpp:134-136).
            Assert.All(specials, a => Assert.Equal(expected switch
            {
                U => "bgrnd51", Si => "bgrnd52", _ => "bgrnd53",
            }, a.ModelName));
        }
    }

    /// <summary>⭐ Two enabled paths cycle between exactly those two classes from
    /// the drawn offset (<c>asteroidigc.cpp:211-225</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(true, true, false)]    // c=3 → U / Si
    [InlineData(true, false, true)]    // c=5 → U / C
    [InlineData(false, true, true)]    // c=6 → Si / C
    public void Generated_two_path_map_alternates_the_two_enabled_classes(
        bool expansion, bool tactical, bool supremacy)
    {
        Require.Artwork();
        var code = Code(expansion, tactical, supremacy);
        var legal = new[] { NativeCapability(code, 1), NativeCapability(code, 2) };
        foreach (var type in new[] { MapGenType.SingleRing, MapGenType.PinWheel })
        {
            var map = GenerateWithPaths(seed: 991, type, expansion, tactical, supremacy);
            foreach (var cluster in map.Clusters)
            {
                var observed = map.Asteroids.Where(a => a.ClusterId == cluster.ClusterId && IsSpecial(a))
                    .Select(a => a.AabmCapabilities).ToArray();
                if (observed.Length == 0) continue;
                Assert.All(observed, a => Assert.Contains(a, legal));
                Assert.Contains(observed, a => a == legal[0]);
                Assert.Contains(observed, a => a == legal[1]);
                var expected = LegalSequences(code, observed.Length)
                    .Select(s => s.Split(',')).ToArray();
                Assert.Contains(expected, seq => seq.SequenceEqual(observed.Select(KindName)));
            }
        }
    }

    /// <summary>⭐ RNG draw count for the 1-class cases: with one path enabled the
    /// native rule draws nothing, so an expansion-only generated map is
    /// bit-for-bit the map the base generator produced for the same seed (which
    /// placed no path-specific rock and drew no offset). The pinned hashes are the
    /// pre-change census of these seeds (docs/reports/P939_REPORT.md §evidence).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Single_path_maps_keep_the_base_map_exactly()
    {
        Require.Artwork();
        var singleRing = GenerateWithPaths(1123059318, MapGenType.SingleRing, expansion: true, tactical: false, supremacy: false);
        Assert.Equal("CE5D9DC09A5CADB30A6F2ECB01F2D6691DF188C17139A5A49E89EB87AD3EE086", FingerprintHash(singleRing));
        Assert.All(singleRing.Asteroids.Where(IsSpecial), a => Assert.Equal(U, a.AabmCapabilities));

        var pinWheel = GenerateWithPaths(939, MapGenType.PinWheel, expansion: true, tactical: false, supremacy: false);
        Assert.Equal("54C03DF1D1A4B98673B87200B776978623BFDFBF3C57057BE1A18364D90E952D", FingerprintHash(pinWheel));
        Assert.All(pinWheel.Asteroids.Where(IsSpecial), a => Assert.Equal(U, a.AabmCapabilities));
    }

    private static string KindName(short capability) => capability switch
    {
        U => "U",
        Si => "Si",
        C => "C",
        _ => "?",
    };

    // ── File maps: unchanged rocks ──────────────────────────────────────────

    private static StaticCore SyntheticCore()
    {
        var records = new List<IgcRecord>();
        var body = new List<byte>();
        void Add(IgcObjectType type, byte[] bytes)
        {
            records.Add(new(type, bytes.Length, body.Count));
            body.AddRange(bytes);
        }
        var constants = new byte[DataConstantsIGC.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(0), .2f);      // c_fcidLensMultiplier
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(4), 10000f);  // c_fcidRadiusUniverse
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(constants.AsSpan(36), 1000f);  // c_fcidAmountHe3
        Add(IgcObjectType.Constants, constants);
        Add(IgcObjectType.Civilization, new byte[DataCivilizationIGC.Size]);
        Add(IgcObjectType.StationType, new byte[DataStationTypeIGC.Size]);
        return StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize,
            Version = 1,
            Records = records,
            Body = body.ToArray(),
            BodyLength = body.Count,
            BytesConsumed = body.Count,
        });
    }

    private static Mission FileMapMission(ResearchSettings research)
    {
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]);
        return Mission.Assemble(SyntheticCore(), MissionMap.FromLists(
            [cluster with { ClusterId = 1, HomeSector = true },
             cluster with { ClusterId = 2 }, cluster with { ClusterId = 3 }], [], [], []),
            [new SideSpec(0, "A", 0)], new NullIgcSite(), research: research);
    }

    /// <summary>⭐ File maps keep the rocks they had: the same native rule
    /// (<c>common.cpp:3052-3058</c>) for every one of the 8 combinations, now
    /// shared with the generated path. Four specials per sector make each sector's
    /// sequence show the cycle and its drawn offset.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void File_map_population_keeps_the_native_special_classes(bool expansion, bool tactical, bool supremacy)
    {
        var mission = FileMapMission(new ResearchSettings
        {
            AllowExpansionPath = expansion,
            AllowTacticalPath = tactical,
            AllowSupremacyPath = supremacy,
        });
        MissionMapGenerator.PopulateFileMapAsteroids(mission, new MapGenParams
        {
            Seed = 864,
            PlayerSectorMineableAsteroids = 0, NeutralSectorMineableAsteroids = 0,
            PlayerSectorSpecialAsteroids = 4, NeutralSectorSpecialAsteroids = 4,
            PlayerSectorAsteroids = 0, NeutralSectorAsteroids = 0,
        });

        var code = Code(expansion, tactical, supremacy);
        var legal = LegalSequences(code, 4);
        foreach (var sector in mission.Sectors)
        {
            var observed = sector.Asteroids.Select(a => a.AabmCapabilities).ToArray();
            Assert.Equal(4, observed.Length);
            Assert.All(observed, a => Assert.Contains(a, new[] { U, Si, C }));
            Assert.Contains(legal, seq => seq == string.Join(",", observed.Select(KindName)));
            if (NativeCount[code] == 1)
                Assert.All(observed, a => Assert.Equal(NativeCapability(code, 1), a));
        }
    }

    /// <summary>File and generated maps of the same mission settings place the
    /// same special classes: one rule, two call sites (<c>common.cpp:3052-3058</c>
    /// shared by <c>mapmakerigc.cpp:944-948</c>).</summary>
    [Fact]
    public void File_and_generated_paths_agree_on_the_special_classes()
    {
        foreach (var (expansion, tactical, supremacy) in new[]
        {
            (false, false, false), (true, false, false), (false, true, false), (true, true, false),
            (false, false, true), (true, false, true), (false, true, true), (true, true, true),
        })
        {
            var code = Code(expansion, tactical, supremacy);
            var mission = FileMapMission(new ResearchSettings
            {
                AllowExpansionPath = expansion,
                AllowTacticalPath = tactical,
                AllowSupremacyPath = supremacy,
            });
            MissionMapGenerator.PopulateFileMapAsteroids(mission, new MapGenParams
            {
                Seed = 864,
                PlayerSectorMineableAsteroids = 0, NeutralSectorMineableAsteroids = 0,
                PlayerSectorSpecialAsteroids = 4, NeutralSectorSpecialAsteroids = 4,
                PlayerSectorAsteroids = 0, NeutralSectorAsteroids = 0,
            });
            var fileKinds = mission.Sectors.SelectMany(s => s.Asteroids).Select(a => a.AabmCapabilities).Distinct()
                .OrderBy(c => c).ToArray();
            var native = Enumerable.Range(1, NativeCount[code]).Select(i => NativeCapability(code, i)).Distinct()
                .OrderBy(c => c).ToArray();
            Assert.Equal(native, fileKinds);
        }
    }
}
